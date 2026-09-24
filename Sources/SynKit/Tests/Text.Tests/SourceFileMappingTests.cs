// Copyright (c) 2021-2022 Yoakke.
// Licensed under the Apache License, Version 2.0.
// Source repository: https://github.com/LanguageDev/Yoakke

using System;
using Xunit;

namespace Yoakke.SynKit.Text.Tests;

/// <summary>
/// Counterexamples for the unified line/column &lt;-&gt; offset mapping of source files.
/// All of these go through the public <see cref="SourceFileExtensions"/> entry points.
/// </summary>
public class SourceFileMappingTests
{
    // Mixed newline styles: CRLF, CR and LF in a single file
    private const string MixedNewlineText = "abc\r\nxy\rqwe\nz";

    [Fact]
    public void OffsetToPositionRoundtripsOnMixedNewlines()
    {
        var sourceFile = new SourceFile("mixed.txt", MixedNewlineText);
        // Force the whole file to be read
        _ = sourceFile.AvailableLines;
        var totalLength = 0;
        for (var line = 0; line < sourceFile.AvailableLines; ++line) totalLength += sourceFile.GetLine(line).Length;

        // Every valid offset must map to a position that maps back to the same offset
        for (var offset = 0; offset <= totalLength; ++offset)
        {
            var position = sourceFile.GetPosition(offset);
            Assert.Equal(offset, sourceFile.GetOffset(position));
        }
    }

    [Fact]
    public void LineStartsMapToExpectedPositions()
    {
        var sourceFile = new SourceFile("mixed.txt", MixedNewlineText);
        var offset = 0;
        for (var line = 0; line < sourceFile.AvailableLines; ++line)
        {
            // The first character of each line maps back and forth
            Assert.Equal(offset, sourceFile.GetOffset(new Position(line, 0)));
            Assert.Equal(new Position(line, 0), sourceFile.GetPosition(offset));
            offset += sourceFile.GetLine(line).Length;
        }
        // The end of the available text maps to the end of the last line
        var lastLine = sourceFile.AvailableLines - 1;
        Assert.Equal(new Position(lastLine, sourceFile.GetLine(lastLine).Length), sourceFile.GetPosition(offset));
    }

    [Fact]
    public void EndOfFileOffsetMapsToEndOfLastLine()
    {
        // A file not ending in a newline
        var sourceFile = new SourceFile("eof.txt", "line1\nline2");
        var totalLength = 0;
        for (var line = 0; line < sourceFile.AvailableLines; ++line) totalLength += sourceFile.GetLine(line).Length;
        var endPosition = sourceFile.GetPosition(totalLength);
        Assert.Equal(sourceFile.AvailableLines - 1, endPosition.Line);
        Assert.Equal(sourceFile.GetLine(sourceFile.AvailableLines - 1).Length, endPosition.Column);
    }

    [Fact]
    public void SurrogatePairsCountAsTwoUtf16Units()
    {
        var sourceFile = new SourceFile("emoji.txt", "a\U0001F600b");
        // The character after the surrogate pair is at column 3 in UTF-16 code units
        Assert.Equal(3, sourceFile.GetPosition(3).Column);
        Assert.Equal(3, sourceFile.GetOffset(new Position(0, 3)));
    }

    [Fact]
    public void NegativeOffsetIsRejected()
    {
        var sourceFile = new SourceFile("a.txt", "abc");
        Assert.Throws<ArgumentOutOfRangeException>(() => sourceFile.GetPosition(-1));
    }

    [Fact]
    public void OffsetBeyondTextIsRejected()
    {
        var sourceFile = new SourceFile("a.txt", "abc");
        var totalLength = 0;
        for (var line = 0; line < sourceFile.AvailableLines; ++line) totalLength += sourceFile.GetLine(line).Length;
        Assert.Throws<ArgumentOutOfRangeException>(() => sourceFile.GetPosition(totalLength + 1));
    }

    [Fact]
    public void InvalidPositionsAreRejected()
    {
        var sourceFile = new SourceFile("a.txt", "abc");
        Assert.Throws<ArgumentOutOfRangeException>(() => sourceFile.GetOffset(new Position(-1, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => sourceFile.GetOffset(new Position(0, -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => sourceFile.GetOffset(new Position(1, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => sourceFile.GetOffset(new Position(0, 100)));
    }
}
