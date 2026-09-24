// Copyright (c) 2021-2022 Yoakke.
// Licensed under the Apache License, Version 2.0.
// Source repository: https://github.com/LanguageDev/Yoakke

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Yoakke.Streams;
using Yoakke.SynKit.Lexer.Attributes;
using Yoakke.SynKit.Text;
using IgnoreAttribute = Yoakke.SynKit.Lexer.Attributes.IgnoreAttribute;
using Range = Yoakke.SynKit.Text.Range;

namespace Yoakke.SynKit.Lexer.Tests;

/// <summary>
/// Cross-component guards: a handwritten lexer and a generated lexer must produce the exact
/// same source spans for the same inputs, because both sit on the unified position tracking
/// boundary. The inputs are adversarial on purpose (mixed newlines, tabs, surrogate pairs,
/// end of file, zero-width tokens, consecutive error recovery).
/// </summary>
public partial class UnifiedSourceSpanTests
{
    public enum TokenType
    {
        [Ignore] [Regex(Regexes.Whitespace)] Whitespace,
        [Error] Error,
        [End] End,
        [Regex(Regexes.Identifier)] Identifier,
        [Regex(Regexes.IntLiteral)] Number,
        [Token("+")] Plus,
    }

    [Lexer(typeof(TokenType))]
    internal partial class GeneratedLexer
    {
    }

    /// <summary>
    /// A handwritten lexer with the same token specification as <see cref="GeneratedLexer"/>,
    /// written against the public <see cref="ICharStream"/> surface only.
    /// </summary>
    private sealed class HandwrittenLexer : ILexer<Token<TokenType>>
    {
        public Position Position => this.charStream.Position;

        public bool IsEnd => this.charStream.IsEnd;

        private readonly ICharStream charStream;

        public HandwrittenLexer(TextReader reader)
        {
            this.charStream = new TextReaderCharStream(reader);
        }

        public Token<TokenType> Next()
        {
        begin:
            if (this.IsEnd) return this.charStream.ConsumeToken(TokenType.End, 0);
            var peek = this.charStream.Peek('\0');
            if (char.IsWhiteSpace(peek))
            {
                this.charStream.Consume();
                goto begin;
            }
            if (peek == '+') return this.charStream.ConsumeToken(TokenType.Plus, 1);
            if (IsIdentifierStart(peek))
            {
                var length = 1;
                while (IsIdentifierPart(this.charStream.LookAhead(length, '\0'))) ++length;
                return this.charStream.ConsumeToken(TokenType.Identifier, length);
            }
            if (char.IsDigit(peek))
            {
                var length = 1;
                while (char.IsDigit(this.charStream.LookAhead(length, '\0'))) ++length;
                return this.charStream.ConsumeToken(TokenType.Number, length);
            }
            return this.charStream.ConsumeToken(TokenType.Error, 1);
        }

        private static bool IsIdentifierStart(char ch) => ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_';

        private static bool IsIdentifierPart(char ch) => IsIdentifierStart(ch) || char.IsDigit(ch);
    }

    private static List<(TokenType Kind, string Text, Range Range)> LexAll(Func<ILexer<Token<TokenType>>> makeLexer)
    {
        var lexer = makeLexer();
        var result = new List<(TokenType, string, Range)>();
        while (true)
        {
            var token = lexer.Next();
            result.Add((token.Kind, token.Text, token.Range));
            if (token.Kind == TokenType.End) break;
        }
        return result;
    }

    private static void AssertSameSpans(string source)
    {
        var handwritten = LexAll(() => new HandwrittenLexer(new StringReader(source)));
        var generated = LexAll(() => new GeneratedLexer(source));
        Assert.Equal(handwritten, generated);
    }

    public static IReadOnlyList<object[]> AdversarialInputs { get; } = new object[][]
    {
        // Mixed newline styles in a single input
        new object[] { "ab\r\ncd\ref\ngh" },
        // Tab between tokens: occupies exactly one column
        new object[] { "a\tb" },
        // A surrogate pair: one Unicode scalar value, two UTF-16 code units
        new object[] { "ab\U0001F600cd" },
        // End of file right after a token, no trailing newline
        new object[] { "ab" },
        // End of file after a trailing newline: the zero-width end token sits on the next line
        new object[] { "ab\n" },
        // End of file after a trailing CRLF
        new object[] { "ab\r\n" },
        // Consecutive error recovery: each error token advances exactly one column
        new object[] { "a#b##c" },
        // Only errors, then end of file
        new object[] { "###" },
        // Empty input: only the zero-width end token
        new object[] { string.Empty },
    };

    [Theory]
    [MemberData(nameof(AdversarialInputs))]
    public void HandwrittenAndGeneratedLexersAgreeOnSpans(string source) => AssertSameSpans(source);

    [Fact]
    public void TabAdvancesExactlyOneColumn()
    {
        // Regression guard: a tab is not zero-width and does not jump to a tab stop here
        var tokens = LexAll(() => new GeneratedLexer("a\tb"));
        Assert.Equal(new Range(new Position(0, 0), new Position(0, 1)), tokens[0].Range);
        Assert.Equal(new Range(new Position(0, 2), new Position(0, 3)), tokens[1].Range);
    }

    [Fact]
    public void CrLfCountsAsSingleNewline()
    {
        // Regression guard: CRLF must not advance two lines
        var tokens = LexAll(() => new GeneratedLexer("a\r\nb"));
        Assert.Equal(new Range(new Position(0, 0), new Position(0, 1)), tokens[0].Range);
        Assert.Equal(new Range(new Position(1, 0), new Position(1, 1)), tokens[1].Range);
    }

    [Fact]
    public void SurrogatePairCountsAsTwoColumns()
    {
        var tokens = LexAll(() => new GeneratedLexer("a\U0001F600b"));
        // 'a', then two error tokens for the two UTF-16 code units of the emoji, then 'b'
        Assert.Equal(TokenType.Identifier, tokens[0].Kind);
        Assert.Equal(TokenType.Error, tokens[1].Kind);
        Assert.Equal(new Range(new Position(0, 1), new Position(0, 2)), tokens[1].Range);
        Assert.Equal(TokenType.Error, tokens[2].Kind);
        Assert.Equal(new Range(new Position(0, 2), new Position(0, 3)), tokens[2].Range);
        Assert.Equal(TokenType.Identifier, tokens[3].Kind);
        Assert.Equal(new Range(new Position(0, 3), new Position(0, 4)), tokens[3].Range);
    }

    [Fact]
    public void ZeroWidthEndTokenAtEndOfFile()
    {
        var tokens = LexAll(() => new GeneratedLexer("ab\n"));
        var end = tokens[^1];
        Assert.Equal(TokenType.End, end.Kind);
        Assert.Equal(string.Empty, end.Text);
        Assert.Equal(new Range(new Position(1, 0), new Position(1, 0)), end.Range);
    }

    [Fact]
    public void ConsecutiveErrorRecoveryProducesStableAdvancingSpans()
    {
        var tokens = LexAll(() => new GeneratedLexer("##a"));
        Assert.Equal(TokenType.Error, tokens[0].Kind);
        Assert.Equal(new Range(new Position(0, 0), new Position(0, 1)), tokens[0].Range);
        Assert.Equal(TokenType.Error, tokens[1].Kind);
        Assert.Equal(new Range(new Position(0, 1), new Position(0, 2)), tokens[1].Range);
        // Recovery: the valid token after the errors is lexed normally
        Assert.Equal(TokenType.Identifier, tokens[2].Kind);
        Assert.Equal(new Range(new Position(0, 2), new Position(0, 3)), tokens[2].Range);
    }

    [Fact]
    public void EndTokenRepeatsAtSamePositionWithoutDuplicates()
    {
        // Asking for more tokens after the end keeps producing the same zero-width end token
        var lexer = new GeneratedLexer("a");
        lexer.Next();
        var end1 = lexer.Next();
        var end2 = lexer.Next();
        Assert.Equal(end1.Range, end2.Range);
        Assert.Equal(new Range(new Position(0, 1), new Position(0, 1)), end1.Range);
    }

    // A reader that fails once, partway through the input, with a caller-chosen exception
    private sealed class FailOnceReader : TextReader
    {
        private readonly string content;
        private readonly int failAt;
        private readonly Func<Exception> makeException;
        private int offset;
        private bool failed;

        public FailOnceReader(string content, int failAt, Func<Exception> makeException)
        {
            this.content = content;
            this.failAt = failAt;
            this.makeException = makeException;
        }

        public override int Read()
        {
            if (!this.failed && this.offset >= this.failAt)
            {
                this.failed = true;
                throw this.makeException();
            }
            return this.offset >= this.content.Length ? -1 : this.content[this.offset++];
        }
    }

    private sealed class BudgetExceededException : Exception
    {
    }

    public static IReadOnlyList<object[]> CausalFailures { get; } = new object[][]
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
    public void FailureKeepsCausalErrorAndRetryIsStable(Func<Exception> makeException)
    {
        const string source = "ab\r\ncd + ef";
        var expected = LexAll(() => new GeneratedLexer(source));

        // The failure must propagate with its causal error type, not wrapped or swallowed
        var exception = Assert.ThrowsAny<Exception>(() =>
            LexAll(() => new GeneratedLexer(new TextReaderCharStream(
                new SourceFile("test.txt", new FailOnceReader(source, 3, makeException))))));
        Assert.Equal(makeException().GetType(), exception.GetType());

        // A retry over the exact same input must produce the exact same spans;
        // no cache, counter or temporary state from the failed attempt leaks into it
        var retried = LexAll(() => new GeneratedLexer(source));
        Assert.Equal(expected, retried);
    }
}
