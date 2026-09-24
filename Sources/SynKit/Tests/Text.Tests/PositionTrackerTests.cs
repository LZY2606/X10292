// Copyright (c) 2021-2022 Yoakke.
// Licensed under the Apache License, Version 2.0.
// Source repository: https://github.com/LanguageDev/Yoakke

using System.Collections.Generic;
using Xunit;

namespace Yoakke.SynKit.Text.Tests;

/// <summary>
/// Counterexamples for the unified position advancing semantics.
/// All of these go through the public <see cref="PositionTracker"/> entry point that both
/// handwritten and generated lexers share.
/// </summary>
public class PositionTrackerTests
{
    private static Position PositionOf(string consumed)
    {
        var tracker = new PositionTracker();
        foreach (var ch in consumed) tracker.Consume(ch);
        return tracker.Position;
    }

    public static IReadOnlyList<object[]> PositionsAfterTextData { get; } = new object[][]
    {
        // Empty input stays at the origin
        new object[] { string.Empty, new Position(0, 0) },
        // Plain ASCII advances one column per character
        new object[] { "abc", new Position(0, 3) },
        // LF advances exactly one line
        new object[] { "a\nb", new Position(1, 1) },
        // CR alone is a newline too (classic Mac-style)
        new object[] { "a\rb", new Position(1, 1) },
        // CRLF is a single newline, not two - a naive per-character implementation
        // would count two lines here
        new object[] { "a\r\nb", new Position(1, 1) },
        // Mixed newline styles in one input
        new object[] { "a\r\nb\rc\nd", new Position(3, 1) },
        // LF followed by CR are two separate newlines
        new object[] { "a\n\rb", new Position(2, 1) },
        // A tab occupies exactly one column (display expansion is presentational)
        new object[] { "a\tb", new Position(0, 3) },
        // A tab right after a newline
        new object[] { "\n\tx", new Position(1, 2) },
        // Control characters other than tab and newlines are zero-width
        new object[] { "a\0b", new Position(0, 2) },
        new object[] { "a\vb", new Position(0, 2) },
        // A surrogate pair is a single Unicode scalar value but two UTF-16 code units,
        // and columns are counted in UTF-16 code units
        new object[] { "a\U0001F600b", new Position(0, 4) },
        // Unpaired surrogates still count as one UTF-16 code unit each
        new object[] { "a\uD800b", new Position(0, 3) },
    };

    [Theory]
    [MemberData(nameof(PositionsAfterTextData))]
    public void PositionAfterConsumingText(string text, Position expected) =>
        Assert.Equal(expected, PositionOf(text));

    [Fact]
    public void CrLfSplitAcrossConsumesIsSingleNewline()
    {
        // The CRLF collapsing must work even when the characters are consumed one by one
        var tracker = new PositionTracker();
        tracker.Consume('a');
        Assert.Equal(new Position(1, 0), tracker.Consume('\r'));
        // Consuming the LF of a CRLF pair must not advance anything
        Assert.Equal(new Position(1, 0), tracker.Consume('\n'));
        Assert.Equal(new Position(1, 1), tracker.Consume('b'));
    }

    [Fact]
    public void ResetForgetsPreviousCharacter()
    {
        var tracker = new PositionTracker();
        tracker.Consume('\r');
        tracker.Reset();
        // After a reset the tracker must not consider the '\r' from before the reset,
        // so this '\n' is a lone LF and advances a line
        Assert.Equal(new Position(1, 0), tracker.Consume('\n'));
    }

    [Fact]
    public void StaticAdvanceMatchesTracker()
    {
        // The static advancement helper must agree with the stateful tracker
        var tracker = new PositionTracker();
        var position = new Position(0, 0);
        char? previous = null;
        foreach (var ch in "a\r\nb\tc\U0001F600d")
        {
            Assert.Equal(position, tracker.Position);
            position = PositionTracker.Advance(position, previous, ch);
            tracker.Consume(ch);
            previous = ch;
        }
        Assert.Equal(position, tracker.Position);
    }
}
