// Copyright (c) 2021-2022 Yoakke.
// Licensed under the Apache License, Version 2.0.
// Source repository: https://github.com/LanguageDev/Yoakke

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Yoakke.SynKit.Lexer;
using Yoakke.SynKit.Text;

namespace Yoakke.SynKit.SpanGuard.Tests;

/// <summary>
/// Guards for the unified source spans: the hand-written and the generated lexer must assign
/// identical spans to identical input, and the spans must line up with the line table of the
/// shared <see cref="SourceFile"/>.
/// </summary>
public class UnifiedSourceSpanTests
{
    private static List<Token<GuardTokenKind>> LexAll(ILexer<Token<GuardTokenKind>> lexer)
    {
        var tokens = new List<Token<GuardTokenKind>>();
        while (true)
        {
            var token = lexer.Next();
            tokens.Add(token);
            if (token.Kind == GuardTokenKind.End) break;
        }
        return tokens;
    }

    private static List<Token<GuardTokenKind>> LexGenerated(ISourceFile file) =>
        LexAll(new GuardLexer(new TextReaderCharStream(file)));

    private static List<Token<GuardTokenKind>> LexHandwritten(ISourceFile file) =>
        LexAll(new HandwrittenGuardLexer(file));

    private static IEnumerable<(GuardTokenKind Kind, string Text, Range Range)> Spans(
        IEnumerable<Token<GuardTokenKind>> tokens) =>
        tokens.Select(t => (t.Kind, t.Text, t.Range));

    [Fact]
    public void Handwritten_And_GeneratedLexer_ProduceIdenticalSpans()
    {
        const string text = "alpha beta\r\ngamma\tdelta\n+ 42\rzeta";
        var generated = LexGenerated(new SourceFile("guard.src", text));
        var handwritten = LexHandwritten(new SourceFile("guard.src", text));
        Assert.Equal(Spans(handwritten), Spans(generated));
    }

    [Fact]
    public void MixedNewlines_TokensAfterTerminator_StartAtColumnZero()
    {
        var tokens = LexGenerated(new SourceFile("guard.src", "aa\r\nbb\ncc\rdd"));
        Assert.Equal(
        [
            (GuardTokenKind.Identifier, "aa", new Range(new Position(0, 0), 2)),
            (GuardTokenKind.Identifier, "bb", new Range(new Position(1, 0), 2)),
            (GuardTokenKind.Identifier, "cc", new Range(new Position(2, 0), 2)),
            (GuardTokenKind.Identifier, "dd", new Range(new Position(3, 0), 2)),
            (GuardTokenKind.End, string.Empty, new Range(new Position(3, 2), 0)),
        ], Spans(tokens).ToArray());
    }

    [Fact]
    public void Crlf_And_Lf_ProduceTheSameLogicalPositions()
    {
        var crlf = LexGenerated(new SourceFile("guard.src", "a1\r\nb2"));
        var lf = LexGenerated(new SourceFile("guard.src", "a1\nb2"));
        Assert.Equal(Spans(crlf), Spans(lf));
    }

    [Fact]
    public void EndOfFile_ZeroWidthEndToken_WithoutTrailingNewline()
    {
        var tokens = LexGenerated(new SourceFile("guard.src", "ab"));
        var end = tokens[^1];
        Assert.Equal(GuardTokenKind.End, end.Kind);
        // The end token is a zero-width span exactly at the end of the input
        Assert.Equal(new Range(new Position(0, 2), new Position(0, 2)), end.Range);
        // Zero-width range invariants: it contains nothing and intersects nothing
        Assert.False(end.Range.Contains(end.Range.Start));
        Assert.False(end.Range.Intersects(end.Range));
    }

    [Fact]
    public void Tab_CountsAsExactlyOneColumn_InTokens()
    {
        var tokens = LexGenerated(new SourceFile("guard.src", "a\tb"));
        Assert.Equal(
        [
            (GuardTokenKind.Identifier, "a", new Range(new Position(0, 0), 1)),
            (GuardTokenKind.Identifier, "b", new Range(new Position(0, 2), 1)),
            (GuardTokenKind.End, string.Empty, new Range(new Position(0, 3), 0)),
        ], Spans(tokens).ToArray());
    }

    [Fact]
    public void SurrogatePair_ConsumedAsTwoUtf16Units()
    {
        // U+1F600 is not a letter, so each of its UTF-16 code units is a separate error token
        var tokens = LexGenerated(new SourceFile("guard.src", "\uD83D\uDE00a"));
        Assert.Equal(
        [
            (GuardTokenKind.Error, "\uD83D", new Range(new Position(0, 0), 1)),
            (GuardTokenKind.Error, "\uDE00", new Range(new Position(0, 1), 1)),
            (GuardTokenKind.Identifier, "a", new Range(new Position(0, 2), 1)),
            (GuardTokenKind.End, string.Empty, new Range(new Position(0, 3), 0)),
        ], Spans(tokens).ToArray());
    }

    [Fact]
    public void ConsecutiveErrorRecovery_AdvancesStably_WithoutOverlapOrDuplicates()
    {
        var tokens = LexGenerated(new SourceFile("guard.src", "a$$b"));
        Assert.Equal(
        [
            (GuardTokenKind.Identifier, "a", new Range(new Position(0, 0), 1)),
            (GuardTokenKind.Error, "$", new Range(new Position(0, 1), 1)),
            (GuardTokenKind.Error, "$", new Range(new Position(0, 2), 1)),
            (GuardTokenKind.Identifier, "b", new Range(new Position(0, 3), 1)),
            (GuardTokenKind.End, string.Empty, new Range(new Position(0, 4), 0)),
        ], Spans(tokens).ToArray());
        // Recovery always makes progress: each span starts where the previous one ended
        for (var i = 1; i < tokens.Count; ++i) Assert.Equal(tokens[i - 1].Range.End, tokens[i].Range.Start);
        // Re-running the same input yields the identical sequence (no state leaks between runs)
        var rerun = LexGenerated(new SourceFile("guard.src", "a$$b"));
        Assert.Equal(Spans(tokens), Spans(rerun));
    }

    [Fact]
    public void String_And_ReaderBackedSources_ProduceIdenticalSpans_AndIntactLines()
    {
        const string text = "aa\r\nbb";
        var fromString = LexGenerated(new SourceFile("guard.src", text));
        var readerBackedFile = new SourceFile("guard.src", new StringReader(text));
        var fromReader = LexGenerated(readerBackedFile);
        Assert.Equal(Spans(fromString), Spans(fromReader));
        // Cross-component guard: after the lexer consumed the reader-backed file, the line
        // table still describes the same text (no divergent readers behind the scenes)
        Assert.Equal(2, readerBackedFile.AvailableLines);
        Assert.Equal("aa\r\n", readerBackedFile.GetLine(0));
        Assert.Equal("bb", readerBackedFile.GetLine(1));
    }

    [Fact]
    public void PublicPositionFormats_StayCompatible()
    {
        var tokens = LexGenerated(new SourceFile("guard.src", "ab"));
        var end = tokens[^1];
        // The public ToString formats of the position types are part of the compatibility surface
        Assert.Equal("line 1, column 3", end.Range.Start.ToString());
        Assert.Equal("from line 1, column 3 to line 1, column 3", end.Range.ToString());
    }
}
