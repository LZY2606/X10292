// Copyright (c) 2021-2022 Yoakke.
// Licensed under the Apache License, Version 2.0.
// Source repository: https://github.com/LanguageDev/Yoakke

using System;
using System.IO;
using System.Text;
using Xunit;
using Yoakke.SynKit.Reporting.Present;
using Yoakke.SynKit.Text;
using Range = Yoakke.SynKit.Text.Range;

namespace Yoakke.SynKit.Reporting.Tests;

/// <summary>
/// Cross-component guards for the unified diagnostics presentation boundary:
/// two files with the same name, end-of-file and zero-width annotations, tab alignment,
/// legacy output compatibility, and recoverable failures in the middle of rendering.
/// </summary>
public class UnifiedPresenterTests
{
    private static Location Loc(ISourceFile source, int line, int column, int length) =>
        new(source, new Range(new Position(line, column), length));

    private static string Present(Diagnostics diagnostic, ISourceFile? _ = null)
    {
        var result = new StringWriter();
        new TextDiagnosticsPresenter(result).Present(diagnostic);
        return result.ToString();
    }

    [Fact]
    public void TwoFilesWithTheSameNameFormASingleGroup()
    {
        // Two distinct ISourceFile instances that share a path are the same identity:
        // they must produce one deterministic group, not two
        var firstFile = new SourceFile("same.txt", "aaaa");
        var secondFile = new SourceFile("same.txt", "bbbb");
        var diag = new Diagnostics()
            .WithSeverity(Severity.Error)
            .WithCode("E0001")
            .WithMessage("some error")
            .WithSourceInfo(Loc(firstFile, 0, 1, 1), Severity.Error, "from first file")
            .WithSourceInfo(Loc(secondFile, 0, 2, 1), "from second file");
        AssertUtils.AreEqualIgnoreNewlineEncoding(
            @"error[E0001]: some error
  ┌─ same.txt:1:2
  │
1 │ aaaa
  │  ^- from second file
  │  └ from first file
  │
", Present(diag));
    }

    [Fact]
    public void ZeroWidthAnnotationAtEndOfFileIsNotDropped()
    {
        // Regression guard: a zero-width annotation past the last available line
        // (typical for end-of-file errors) used to vanish from the output
        var src = new SourceFile("eof.txt", "abc");
        var diag = new Diagnostics()
            .WithSeverity(Severity.Error)
            .WithCode("E0001")
            .WithMessage("unexpected end")
            .WithSourceInfo(Loc(src, 1, 0, 0), Severity.Error, "here");
        AssertUtils.AreEqualIgnoreNewlineEncoding(
            @"error[E0001]: unexpected end
  ┌─ eof.txt:2:1
  │
1 │ abc
  │ ^ here
  │
", Present(diag));
    }

    [Fact]
    public void ZeroWidthAnnotationAfterTrailingNewline()
    {
        // The file ends with a newline, so the end-of-file position is on a line
        // that does not exist in the source at all
        var src = new SourceFile("eof.txt", "abc\n");
        var diag = new Diagnostics()
            .WithSeverity(Severity.Error)
            .WithCode("E0001")
            .WithMessage("unexpected end")
            .WithSourceInfo(Loc(src, 1, 0, 0), Severity.Error, "here");
        AssertUtils.AreEqualIgnoreNewlineEncoding(
            @"error[E0001]: unexpected end
  ┌─ eof.txt:2:1
  │
1 │ abc
  │ ^ here
  │
", Present(diag));
    }

    [Fact]
    public void ZeroWidthAnnotationInsideLineGetsSingleArrowHead()
    {
        var src = new SourceFile("zw.txt", "abc");
        var diag = new Diagnostics()
            .WithSeverity(Severity.Error)
            .WithCode("E0001")
            .WithMessage("missing something")
            .WithSourceInfo(Loc(src, 0, 1, 0), Severity.Error, "missing");
        AssertUtils.AreEqualIgnoreNewlineEncoding(
            @"error[E0001]: missing something
  ┌─ zw.txt:1:2
  │
1 │ abc
  │  ^ missing
  │
", Present(diag));
    }

    [Fact]
    public void TabInSourceLineKeepsAnnotationAligned()
    {
        // The source line expands the tab to the tab stop, the annotation column
        // counts the tab as a single character; the arrow must still land under 'b'
        var src = new SourceFile("tab.txt", "a\tb");
        var diag = new Diagnostics()
            .WithSeverity(Severity.Error)
            .WithCode("E0001")
            .WithMessage("tab test")
            .WithSourceInfo(Loc(src, 0, 2, 1), Severity.Error, "under b");
        AssertUtils.AreEqualIgnoreNewlineEncoding(
            @"error[E0001]: tab test
  ┌─ tab.txt:1:3
  │
1 │ a   b
  │     ^ under b
  │
", Present(diag));
    }

    [Fact]
    public void LegacyDiagnosticTextIsUnchanged()
    {
        // The exact output a normal, pre-existing input produced before the unification
        // must be preserved byte-for-byte
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
            .WithSourceInfo(Loc(src, 2, 10, 4), Severity.Error, "some annotation");
        AssertUtils.AreEqualIgnoreNewlineEncoding(
            @"error[E0001]: Some error message
  ┌─ simple.txt:3:11
  │
2 │ prev line
3 │ this is a line of text
  │           ^^^^ some annotation
4 │ next line
  │
", Present(diag));
    }

    [Fact]
    public void MergedDiagnosticsPresentInStableOrderWithoutDuplicates()
    {
        // Consecutive error recovery produces repeated zero-width diagnostics at the
        // same position; merged and presented they must appear exactly once, in order
        var src = new SourceFile("recovery.txt", "abc");
        Diagnostics Recovered(string message) => new Diagnostics()
            .WithSeverity(Severity.Error)
            .WithMessage("parse error")
            .WithSourceInfo(Loc(src, 0, 3, 0), Severity.Error, message);
        var merged = DiagnosticsMerger.Merge(
            Recovered("expected expression"),
            Recovered("expected expression"),
            Recovered("expected expression"));
        AssertUtils.AreEqualIgnoreNewlineEncoding(
            @"error: parse error
  ┌─ recovery.txt:1:4
  │
1 │ abc
  │    ^ expected expression
  │
", Present(merged));
    }

    // A writer that fails once, partway through the output, then recovers
    private sealed class FlakyWriter : StringWriter
    {
        private readonly int failAfter;
        private int written;
        private bool failed;

        public FlakyWriter(int failAfter)
        {
            this.failAfter = failAfter;
        }

        public override void Write(string? value)
        {
            if (!this.failed && value is not null && this.written + value.Length > this.failAfter)
            {
                this.failed = true;
                throw new IOException("simulated transient output failure");
            }
            if (value is not null) this.written += value.Length;
            base.Write(value);
        }
    }

    [Fact]
    public void RecoverableFailureDuringPresentLeavesNoStateBehind()
    {
        var src = new SourceFile("simple.txt", "hello world");
        var diag = new Diagnostics()
            .WithSeverity(Severity.Error)
            .WithCode("E0001")
            .WithMessage("some error")
            .WithSourceInfo(Loc(src, 0, 2, 3), Severity.Error, "some annotation");

        // Reference output from a presenter that never fails
        var expected = Present(diag);

        // The same presenter is reused after a recoverable failure in the middle of rendering
        var flakyWriter = new FlakyWriter(failAfter: 10);
        var presenter = new TextDiagnosticsPresenter(flakyWriter);
        Assert.Throws<IOException>(() => presenter.Present(diag));

        // The caller discards the partially written, corrupt sink content
        flakyWriter.GetStringBuilder().Clear();

        // The retry must produce byte-identical output: no leftover buffer, counter or
        // temporary state from the failed call may leak into the successful one
        presenter.Present(diag);
        Assert.Equal(expected, flakyWriter.ToString());
    }

    // A reader that fails with a caller-chosen exception when a line is requested
    private sealed class FailingReader : TextReader
    {
        private readonly Func<Exception> makeException;

        public FailingReader(Func<Exception> makeException)
        {
            this.makeException = makeException;
        }

        public override string? ReadLine() => throw this.makeException();
    }

    private sealed class BudgetExceededException : Exception
    {
    }

    public static System.Collections.Generic.IReadOnlyList<object[]> CausalFailures { get; } = new object[][]
    {
        // Corrupted input
        new object[] { (Func<Exception>)(() => new IOException("corrupted input")) },
        // Caller cancellation
        new object[] { (Func<Exception>)(() => new OperationCanceledException()) },
        // Resource budget exhaustion
        new object[] { (Func<Exception>)(() => new BudgetExceededException()) },
    };

    [Theory]
    [MemberData(nameof(CausalFailures))]
    public void SourceFailureKeepsCausalError(Func<Exception> makeException)
    {
        // A failure inside the source while rendering must surface with its causal
        // error type, not wrapped into some generic rendering failure
        var src = new SourceFile("broken.txt", new FailingReader(makeException));
        var diag = new Diagnostics()
            .WithSeverity(Severity.Error)
            .WithMessage("some error")
            .WithSourceInfo(Loc(src, 0, 0, 1), Severity.Error, "some annotation");
        var exception = Assert.ThrowsAny<Exception>(() => Present(diag));
        Assert.Equal(makeException().GetType(), exception.GetType());
    }
}
