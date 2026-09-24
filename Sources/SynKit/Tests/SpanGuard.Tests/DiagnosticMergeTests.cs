// Copyright (c) 2021-2022 Yoakke.
// Licensed under the Apache License, Version 2.0.
// Source repository: https://github.com/LanguageDev/Yoakke

using System.IO;
using System.Linq;
using Xunit;
using Yoakke.SynKit.Reporting;
using Yoakke.SynKit.Reporting.Present;
using Yoakke.SynKit.Text;

namespace Yoakke.SynKit.SpanGuard.Tests;

/// <summary>
/// Guards for the unified diagnostic merging boundary: source informations are merged
/// (deduplicated), ordered and grouped in exactly one place - the presenter - instead of
/// every layer re-interpreting empty ranges and file identity on its own.
/// </summary>
public class DiagnosticMergeTests
{
    private static string Render(Diagnostics diag)
    {
        var writer = new StringWriter();
        new TextDiagnosticsPresenter(writer).Present(diag);
        return writer.ToString().Replace("\r\n", "\n");
    }

    private static Location Loc(ISourceFile file, int line, int column, int length) =>
        new(file, new Range(new Position(line, column), length));

    private static Location ZeroWidth(ISourceFile file, int line, int column) =>
        new(file, new Range(new Position(line, column), new Position(line, column)));

    [Fact]
    public void LegacyNormalInput_DiagnosticTextIsUnchanged()
    {
        // The text a pre-unification implementation produced for normal input must not move
        var src = new SourceFile(
            "simple.txt",
@"line 1
prev line
this is a line of text
next line
some other line");
        var diag = new Diagnostics()
            .WithSeverity(Severity.Error)
            .WithCode("E0001")
            .WithMessage("Some error message")
            .WithSourceInfo(Loc(src, line: 2, column: 10, length: 4), Severity.Error, "some annotation");
        Assert.Equal(
@"error[E0001]: Some error message
  ┌─ simple.txt:3:11
  │
2 │ prev line
3 │ this is a line of text
  │           ^^^^ some annotation
4 │ next line
  │
", Render(diag));
    }

    [Fact]
    public void ZeroWidthAnnotation_RendersAVisibleMarker()
    {
        var src = new SourceFile("zw.txt", "abc\ndef");
        var diag = new Diagnostics()
            .WithSeverity(Severity.Error)
            .WithMessage("zero width")
            .WithSourceInfo(ZeroWidth(src, line: 0, column: 3), Severity.Error, "here");
        var rendered = Render(diag);
        // A zero-width span still points at a visible position instead of vanishing
        Assert.Equal(
@"error: zero width
  ┌─ zw.txt:1:4
  │
1 │ abc
  │    ^ here
2 │ def
  │
", rendered);
    }

    [Fact]
    public void DuplicateAnnotations_AreMergedIntoASingleMarker()
    {
        var src = new SourceFile("dup.txt", "abc");
        Diagnostics Build(int copies)
        {
            var diag = new Diagnostics()
                .WithSeverity(Severity.Error)
                .WithMessage("dup");
            for (var i = 0; i < copies; ++i)
            {
                diag.WithSourceInfo(ZeroWidth(src, line: 0, column: 1), Severity.Error, "here");
            }
            return diag;
        }
        var once = Render(Build(1));
        var thrice = Render(Build(3));
        // Exact duplicates of the same diagnostic information collapse into one
        Assert.Equal(once, thrice);
        Assert.Equal(1, thrice.Split('^').Length - 1);
    }

    [Fact]
    public void RecoveryZeroWidthDiagnostics_StableOrder_NoDuplicates()
    {
        var src = new SourceFile("rec.txt", "ab\ncd\nef");
        Diagnostics BuildScrambled(bool flip)
        {
            var diag = new Diagnostics()
                .WithSeverity(Severity.Error)
                .WithMessage("recovery");
            var infos = new[]
            {
                (Info: ZeroWidth(src, 2, 1), Message: "at-f"),
                (Info: ZeroWidth(src, 0, 2), Message: "after-b"),
                (Info: ZeroWidth(src, 2, 1), Message: "at-f"), // exact duplicate from a repeated recovery step
                (Info: ZeroWidth(src, 1, 0), Message: "before-c"),
            };
            if (flip) infos = infos.Reverse().ToArray();
            foreach (var (location, message) in infos) diag.WithSourceInfo(location, Severity.Error, message);
            return diag;
        }
        var forward = Render(BuildScrambled(flip: false));
        var backward = Render(BuildScrambled(flip: true));
        // Insertion order and repeated recovery steps do not change the merged output
        Assert.Equal(forward, backward);
        Assert.Equal(3, forward.Split('^').Length - 1);
        Assert.Equal(
@"error: recovery
  ┌─ rec.txt:1:3
  │
1 │ ab
  │    ^ after-b
2 │ cd
  │ ^ before-c
3 │ ef
  │  ^ at-f
  │
", forward);
    }

    [Fact]
    public void TwoFilesWithTheSameName_MergeIntoASingleGroup()
    {
        // Two distinct ISourceFile instances denoting the same path are the same logical file
        var first = new SourceFile("same.txt", "x = 1\ny = 2");
        var second = new SourceFile("same.txt", "x = 1\ny = 2");
        var diag = new Diagnostics()
            .WithSeverity(Severity.Error)
            .WithMessage("same name")
            .WithSourceInfo(Loc(first, line: 0, column: 0, length: 1), Severity.Error, "first")
            .WithSourceInfo(Loc(second, line: 1, column: 0, length: 1), Severity.Error, "second");
        var rendered = Render(diag);
        // A single group header for the shared path, annotations ordered by position
        Assert.Equal(1, rendered.Split("┌─").Length - 1);
        Assert.Equal(
@"error: same name
  ┌─ same.txt:1:1
  │
1 │ x = 1
  │ ^ first
2 │ y = 2
  │ ^ second
  │
", rendered);
    }

    [Fact]
    public void DifferentFiles_GroupsAppearInFirstEncounterOrder_Deterministically()
    {
        var fileB = new SourceFile("b.txt", "bb");
        var fileA = new SourceFile("a.txt", "aa");
        Diagnostics Build()
        {
            return new Diagnostics()
                .WithSeverity(Severity.Error)
                .WithMessage("two files")
                .WithSourceInfo(Loc(fileB, 0, 0, 1), Severity.Error, "in-b")
                .WithSourceInfo(Loc(fileA, 0, 0, 1), Severity.Error, "in-a");
        }
        var first = Render(Build());
        var second = Render(Build());
        Assert.Equal(first, second);
        // Groups follow the order their first annotation appeared in
        Assert.True(first.IndexOf("b.txt", System.StringComparison.Ordinal)
            < first.IndexOf("a.txt", System.StringComparison.Ordinal));
    }
}
