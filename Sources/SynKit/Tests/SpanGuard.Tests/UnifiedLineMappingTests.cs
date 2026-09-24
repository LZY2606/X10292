// Copyright (c) 2021-2022 Yoakke.
// Licensed under the Apache License, Version 2.0.
// Source repository: https://github.com/LanguageDev/Yoakke

using System;
using System.IO;
using Xunit;
using Yoakke.SynKit.Lexer;
using Yoakke.SynKit.Text;

namespace Yoakke.SynKit.SpanGuard.Tests;

/// <summary>
/// Guards for the unified line mapping: <see cref="SourceFile"/> is the single authority on
/// newline interpretation, and every consumer observes the same text through the same buffer.
/// Units: lines are delimited by \r\n, \r or \n (one terminator each); columns are UTF-16
/// code units; a tab is exactly 1 column.
/// </summary>
public class UnifiedLineMappingTests
{
    [Fact]
    public void MixedNewlines_EachTerminator_CountsAsExactlyOneLineBreak()
    {
        var text = "a\r\nb\nc\rd";
        foreach (var file in new[]
        {
            new SourceFile("mixed.txt", text),
            new SourceFile("mixed.txt", new StringReader(text)),
        })
        {
            Assert.Equal(4, file.AvailableLines);
            // The original text is preserved verbatim, terminators are not re-encoded
            Assert.Equal("a\r\n", file.GetLine(0));
            Assert.Equal("b\n", file.GetLine(1));
            Assert.Equal("c\r", file.GetLine(2));
            Assert.Equal("d", file.GetLine(3));
            Assert.Equal(text, file.ReadToEnd());
        }
    }

    [Fact]
    public void MixedNewlines_CharStreamPositions_AlignWithLineTable()
    {
        var file = new SourceFile("mixed.txt", "a\r\nb\nc\rd");
        var stream = new TextReaderCharStream(file);
        var positions = new System.Collections.Generic.List<(char Char, Position After)>();
        while (stream.TryConsume(out var ch)) positions.Add((ch, stream.Position));
        Assert.Equal(
        [
            ('a', new Position(0, 1)),
            ('\r', new Position(1, 0)),
            ('\n', new Position(1, 0)), // \r\n is a single terminator
            ('b', new Position(1, 1)),
            ('\n', new Position(2, 0)),
            ('c', new Position(2, 1)),
            ('\r', new Position(3, 0)),
            ('d', new Position(3, 1)),
        ], positions);
        Assert.True(stream.IsEnd);
        // The line table agrees with the positions the stream produced
        Assert.Equal(4, file.AvailableLines);
    }

    [Fact]
    public void EndOfFile_WithoutTrailingNewline_AddsNoPhantomTerminator()
    {
        var file = new SourceFile("eof.txt", "abc");
        Assert.Equal(1, file.AvailableLines);
        Assert.Equal("abc", file.GetLine(0));
        Assert.Equal("abc", file.ReadToEnd());
        // The end of input is at column 3 of line 0, not on a phantom next line
        var stream = new TextReaderCharStream(file);
        Assert.Equal(3, stream.Consume(3));
        Assert.True(stream.IsEnd);
        Assert.Equal(new Position(0, 3), stream.Position);
    }

    [Fact]
    public void EndOfFile_WithTrailingNewline_EndsOnNextLineStart()
    {
        var file = new SourceFile("eof.txt", "ab\n");
        Assert.Equal(1, file.AvailableLines);
        Assert.Equal("ab\n", file.GetLine(0));
        var stream = new TextReaderCharStream(file);
        Assert.Equal(3, stream.Consume(3));
        Assert.True(stream.IsEnd);
        Assert.Equal(new Position(1, 0), stream.Position);
    }

    [Fact]
    public void EndOfFile_EmptyFile_HasNoLines()
    {
        var file = new SourceFile("empty.txt", string.Empty);
        Assert.Equal(0, file.AvailableLines);
        Assert.Equal(string.Empty, file.GetLine(0));
        var stream = new TextReaderCharStream(file);
        Assert.True(stream.IsEnd);
        Assert.Equal(new Position(0, 0), stream.Position);
    }

    [Fact]
    public void SurrogatePair_OccupiesTwoUtf16Columns()
    {
        // "a", U+1F600 (a surrogate pair), "b"
        var file = new SourceFile("emoji.txt", "a\uD83D\uDE00b");
        var stream = new TextReaderCharStream(file);
        var positions = new System.Collections.Generic.List<Position>();
        while (stream.TryConsume(out _)) positions.Add(stream.Position);
        // Each UTF-16 code unit advances the column by exactly 1
        Assert.Equal(
        [
            new Position(0, 1),
            new Position(0, 2),
            new Position(0, 3),
            new Position(0, 4),
        ], positions);
        // The original scalar value survives the round-trip through the line table
        Assert.Equal("a\uD83D\uDE00b", file.GetLine(0));
    }

    [Fact]
    public void Tab_AdvancesExactlyOneColumn()
    {
        var file = new SourceFile("tabs.txt", "a\tb");
        var stream = new TextReaderCharStream(file);
        var positions = new System.Collections.Generic.List<(char Char, Position After)>();
        while (stream.TryConsume(out var ch)) positions.Add((ch, stream.Position));
        Assert.Equal(
        [
            ('a', new Position(0, 1)),
            ('\t', new Position(0, 2)),
            ('b', new Position(0, 3)),
        ], positions);
    }

    [Fact]
    public void RecoverableFailure_LeavesNoHalfUpdatedState_AndRetryMatchesControl()
    {
        const string text = "first\nsecond\nthird";
        // Fails on the 16th read call, which is in the middle of the third line
        var reader = new FailingTextReader(text, new IOException("transient read failure"), failAtCall: 16);
        var file = new SourceFile("flaky.txt", reader);

        Assert.Equal("first\n", file.GetLine(0));
        var linesBeforeFailure = file.AvailableLines;

        var thrown = Record.Exception(() => file.GetLine(2));
        Assert.NotNull(thrown);
        Assert.Equal(1, reader.FailureCount);

        // The failure did not half-update the shared state: the line table and the
        // committed lines look exactly like before the failed call
        Assert.Equal(linesBeforeFailure, file.AvailableLines);
        Assert.Equal("first\n", file.GetLine(0));
        Assert.Equal("second\n", file.GetLine(1));

        // Retrying the exact same call now succeeds and matches a never-failing control
        Assert.Equal("third", file.GetLine(2));
        var control = new SourceFile("flaky.txt", text);
        Assert.Equal(control.AvailableLines, file.AvailableLines);
        for (var i = 0; i < control.AvailableLines; ++i) Assert.Equal(control.GetLine(i), file.GetLine(i));
    }

    [Fact]
    public void FailureKinds_KeepTheirCausalErrors()
    {
        var cases = new Exception[]
        {
            new IOException("corrupted input"),
            new OperationCanceledException("cancelled by the caller"),
            new ResourceBudgetExceededException("read budget exhausted"),
        };
        foreach (var scheduled in cases)
        {
            var file = new SourceFile("f.txt", new FailingTextReader("x\ny", scheduled, failAtCall: 1));
            var thrown = Record.Exception(() => file.GetLine(0));
            // The exact causal error propagates, it is not wrapped or replaced
            Assert.Same(scheduled, thrown);
            // The failure is recoverable: the same call succeeds afterwards
            Assert.Equal("x\n", file.GetLine(0));
        }
    }
}
