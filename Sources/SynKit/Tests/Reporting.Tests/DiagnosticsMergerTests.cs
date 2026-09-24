// Copyright (c) 2021-2022 Yoakke.
// Licensed under the Apache License, Version 2.0.
// Source repository: https://github.com/LanguageDev/Yoakke

using System.Collections.Generic;
using System.Linq;
using Xunit;
using Yoakke.SynKit.Text;
using Range = Yoakke.SynKit.Text.Range;

namespace Yoakke.SynKit.Reporting.Tests;

/// <summary>
/// Counterexamples for the unified diagnostics merging boundary.
/// All of these go through the public <see cref="DiagnosticsMerger"/> entry point.
/// </summary>
public class DiagnosticsMergerTests
{
    private static Location Loc(ISourceFile source, int line, int column, int length) =>
        new(source, new Range(new Position(line, column), length));

    private static Diagnostics ErrorAt(ISourceFile source, int line, int column, int length, string message) =>
        new Diagnostics()
            .WithSeverity(Severity.Error)
            .WithMessage("error")
            .WithSourceInfo(Loc(source, line, column, length), Severity.Error, message);

    [Fact]
    public void MergeKeepsStableOrder()
    {
        var src = new SourceFile("a.txt", "abcdef");
        var first = ErrorAt(src, 0, 0, 1, "first");
        var second = ErrorAt(src, 0, 2, 1, "second");
        var third = ErrorAt(src, 0, 4, 1, "third");

        var merged = DiagnosticsMerger.Merge(first, second, third);

        var messages = merged.Information.OfType<SourceDiagnosticInfo>().Select(i => i.Message).ToList();
        Assert.Equal(new[] { "first", "second", "third" }, messages);
    }

    [Fact]
    public void MergeDeduplicatesIdenticalZeroWidthInfos()
    {
        // Error recovery tends to raise the same zero-width diagnostic repeatedly;
        // the merge must keep exactly one, in the original position
        var src = new SourceFile("a.txt", "abc");
        var d1 = ErrorAt(src, 0, 3, 0, "recovered here");
        var d2 = ErrorAt(src, 0, 3, 0, "recovered here");
        var d3 = ErrorAt(src, 0, 3, 0, "recovered here");

        var merged = DiagnosticsMerger.Merge(d1, d2, d3);

        var info = Assert.Single(merged.Information);
        var sourceInfo = Assert.IsType<SourceDiagnosticInfo>(info);
        Assert.Equal("recovered here", sourceInfo.Message);
        Assert.Equal(new Range(new Position(0, 3), new Position(0, 3)), sourceInfo.Location.Range);
    }

    [Fact]
    public void MergeKeepsDistinctInfosAtSamePosition()
    {
        // Same position but different messages are not duplicates
        var src = new SourceFile("a.txt", "abc");
        var d1 = ErrorAt(src, 0, 3, 0, "one");
        var d2 = ErrorAt(src, 0, 3, 0, "two");

        var merged = DiagnosticsMerger.Merge(d1, d2);

        Assert.Equal(2, merged.Information.Count);
    }

    [Fact]
    public void MergeTakesHighestSeverityAndFirstHead()
    {
        var warning = new Diagnostics()
            .WithSeverity(Severity.Warning)
            .WithCode("W0001")
            .WithMessage("a warning");
        var error = new Diagnostics()
            .WithSeverity(Severity.Error)
            .WithCode("E0001")
            .WithMessage("an error");

        var merged = DiagnosticsMerger.Merge(warning, error);

        Assert.Equal(Severity.Error, merged.Severity);
        // The head code and message come from the first input that defines them
        Assert.Equal("W0001", merged.Code);
        Assert.Equal("a warning", merged.Message);
    }

    [Fact]
    public void MergeDoesNotMutateInputs()
    {
        var src = new SourceFile("a.txt", "abc");
        var first = ErrorAt(src, 0, 0, 1, "first");
        var second = ErrorAt(src, 0, 1, 1, "second");
        var firstCount = first.Information.Count;
        var secondCount = second.Information.Count;

        var merged = DiagnosticsMerger.Merge(first, second);

        Assert.Equal(firstCount, first.Information.Count);
        Assert.Equal(secondCount, second.Information.Count);
        Assert.Equal(2, merged.Information.Count);
        // Mutating the merged result must not leak back into the inputs
        merged.Information.Clear();
        Assert.Equal(firstCount, first.Information.Count);
        Assert.Equal(secondCount, second.Information.Count);
    }

    [Fact]
    public void MergeIsSchedulingAndEnumerationIndependent()
    {
        // The same diagnostics, surfaced through different enumeration and scheduling
        // conditions (eager array vs. lazily produced sequence), must merge identically
        var src = new SourceFile("a.txt", "abcdef");
        Diagnostics[] eager =
        {
            ErrorAt(src, 0, 0, 1, "first"),
            ErrorAt(src, 0, 2, 1, "second"),
            ErrorAt(src, 0, 4, 1, "third"),
        };
        IEnumerable<Diagnostics> Lazy()
        {
            yield return ErrorAt(src, 0, 0, 1, "first");
            yield return ErrorAt(src, 0, 2, 1, "second");
            yield return ErrorAt(src, 0, 4, 1, "third");
        }

        var fromArray = DiagnosticsMerger.Merge(eager);
        var fromLazy = DiagnosticsMerger.Merge(Lazy());

        Assert.Equal(
            fromArray.Information.OfType<SourceDiagnosticInfo>().Select(i => (i.Location.Range, i.Message)),
            fromLazy.Information.OfType<SourceDiagnosticInfo>().Select(i => (i.Location.Range, i.Message)));
        Assert.Equal(fromArray.Severity, fromLazy.Severity);
    }

    [Fact]
    public void MergeOfNothingIsEmpty()
    {
        var merged = DiagnosticsMerger.Merge();
        Assert.Null(merged.Severity);
        Assert.Null(merged.Code);
        Assert.Null(merged.Message);
        Assert.Empty(merged.Information);
    }
}
