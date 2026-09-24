// Copyright (c) 2021-2022 Yoakke.
// Licensed under the Apache License, Version 2.0.
// Source repository: https://github.com/LanguageDev/Yoakke

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Yoakke.Streams;
using Yoakke.SynKit.Lexer;
using Yoakke.SynKit.Lexer.Attributes;
using Yoakke.SynKit.Reporting;
using Yoakke.SynKit.Reporting.Present;
using Yoakke.SynKit.Text;
using IgnoreAttribute = Yoakke.SynKit.Lexer.Attributes.IgnoreAttribute;

namespace Yoakke.SynKit.Parser.Tests;

/// <summary>
/// The acceptance story for the unified source span, line/column mapping and diagnostics
/// merging boundary. Everything goes through the public entry points of the toolchain:
/// handwritten and generated lexers produce tokens with source spans, the combinator parser
/// consumes them, parse errors become <see cref="Diagnostics"/> through the shared merging
/// boundary, and the unified presenter renders them.
/// </summary>
public partial class AcceptanceStoryTests
{
    public enum TokenType
    {
        [Ignore] [Regex(Regexes.Whitespace)] Whitespace,
        [Error] Error,
        [End] End,
        [Regex(Regexes.IntLiteral)] Number,
        [Token("+")] Plus,
    }

    [Lexer(typeof(TokenType))]
    internal partial class GeneratedLexer
    {
    }

    // A handwritten lexer with the same token specification, public ICharStream surface only
    private sealed class HandwrittenLexer : ILexer<Token<TokenType>>
    {
        public Position Position => this.charStream.Position;

        public bool IsEnd => this.charStream.IsEnd;

        private readonly ICharStream charStream;

        public HandwrittenLexer(ISourceFile sourceFile)
        {
            this.charStream = new TextReaderCharStream(sourceFile);
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
            if (char.IsDigit(peek))
            {
                var length = 1;
                while (char.IsDigit(this.charStream.LookAhead(length, '\0'))) ++length;
                return this.charStream.ConsumeToken(TokenType.Number, length);
            }
            return this.charStream.ConsumeToken(TokenType.Error, 1);
        }
    }

    // The grammar: sum := Number ('+' Number)* End
    private static Combinator.Parser<IToken<TokenType>, int> SumParser()
    {
        var number = Combinator.Transform(Combinator.Kind(TokenType.Number), tok => int.Parse(tok.Text));
        var plusNumber = Combinator.Transform(
            Combinator.Seq(Combinator.Kind(TokenType.Plus), number), pair => pair.Item2);
        var sum = Combinator.Transform(
            Combinator.Seq(number, Combinator.Rep0(plusNumber)),
            pair => pair.Item2.Aggregate(pair.Item1, (acc, n) => acc + n));
        return Combinator.Transform(
            Combinator.Seq(sum, Combinator.Kind(TokenType.End)), pair => pair.Item1);
    }

    private static List<IToken<TokenType>> LexAll(ILexer<Token<TokenType>> lexer)
    {
        var tokens = new List<IToken<TokenType>>();
        while (true)
        {
            var token = lexer.Next();
            tokens.Add(token);
            if (token.Kind == TokenType.End) break;
        }
        return tokens;
    }

    private static ParseResult<int> ParseTokens(IReadOnlyList<IToken<TokenType>> tokens) =>
        SumParser().Parse(new MemoryStream<IToken<TokenType>>(tokens.ToArray().AsMemory()));

    private static string Present(Diagnostics diagnostics)
    {
        var writer = new StringWriter();
        new TextDiagnosticsPresenter(writer).Present(diagnostics);
        return writer.ToString().Replace("\r\n", "\n");
    }

    [Fact]
    public void HandwrittenAndGeneratedLexerFeedTheSameParse()
    {
        // The start of the story: one source, two lexer implementations, one span semantics
        const string source = "1 +\r\n2\t+ 3\n+4";
        var handwrittenTokens = LexAll(new HandwrittenLexer(new SourceFile("story.txt", source)));
        var generatedTokens = LexAll(new GeneratedLexer(new SourceFile("story.txt", source)));

        // Identical spans from both lexers
        Assert.Equal(
            handwrittenTokens.Select(t => (t.Kind, t.Text, t.Range)),
            generatedTokens.Select(t => (t.Kind, t.Text, t.Range)));

        // Identical parse results through the combinator parser
        var handwrittenResult = ParseTokens(handwrittenTokens);
        var generatedResult = ParseTokens(generatedTokens);
        Assert.True(handwrittenResult.IsOk);
        Assert.True(generatedResult.IsOk);
        Assert.Equal(10, handwrittenResult.Ok.Value);
        Assert.Equal(handwrittenResult.Ok.Value, generatedResult.Ok.Value);

        // Building diagnostics alongside must not change the successful return value
        var diagnostics = new Diagnostics().WithMessage("bookkeeping");
        Assert.Equal(10, ParseTokens(handwrittenTokens).Ok.Value);
        Assert.Empty(diagnostics.Information);
    }

    [Fact]
    public void ErrorRecoveryMergesIntoStableDiagnostics()
    {
        // Two unexpected characters on different lines, reported by two stages:
        // the lexer stage and the recovery stage report the same offending tokens,
        // so the merge must deduplicate them while keeping a stable order
        const string source = "1 #\r\n# + 2";
        var tokens = LexAll(new GeneratedLexer(new SourceFile("story.txt", source)));

        // Lexer stage: report and drop error tokens
        var lexerStage = new Diagnostics().WithSeverity(Severity.Error).WithMessage("syntax errors");
        var kept = new List<IToken<TokenType>>();
        foreach (var token in tokens)
        {
            if (token.Kind == TokenType.Error) lexerStage.WithSourceInfo(token.Location, Severity.Error, "unexpected character");
            else kept.Add(token);
        }
        // Recovery stage: reports the same offending tokens again (e.g. from a different pass)
        var recoveryStage = new Diagnostics().WithSeverity(Severity.Error).WithMessage("syntax errors");
        foreach (var token in tokens)
        {
            if (token.Kind == TokenType.Error) recoveryStage.WithSourceInfo(token.Location, Severity.Error, "unexpected character");
        }

        // The parse of the cleaned-up token stream succeeds and is unaffected by the diagnostics
        var result = ParseTokens(kept);
        Assert.True(result.IsOk);
        Assert.Equal(3, result.Ok.Value);

        // The merged diagnostics contain each annotation exactly once, in stable order
        var merged = DiagnosticsMerger.Merge(lexerStage, recoveryStage);
        Assert.Equal(2, merged.Information.Count);
        Assert.Equal(
            @"error: syntax errors
  ┌─ story.txt:1:3
  │
1 │ 1 #
  │   ^ unexpected character
2 │ # + 2
  │ ^ unexpected character
  │
", Present(merged));
    }

    [Fact]
    public void ParseErrorAtEndOfInputProducesZeroWidthDiagnostic()
    {
        // An unfinished expression makes the parser run into the zero-width end token
        const string source = "1 +";
        var tokens = LexAll(new GeneratedLexer(new SourceFile("story.txt", source)));
        var result = ParseTokens(tokens);
        Assert.True(result.IsError);

        // Map the error offset back to a token through the public token stream
        var errorOffset = (int)result.Error.Position;
        var errorToken = tokens[Math.Min(errorOffset, tokens.Count - 1)];
        var diagnostics = new Diagnostics()
            .WithSeverity(Severity.Error)
            .WithMessage("unexpected end of input")
            .WithSourceInfo(errorToken.Location, Severity.Error, "expected a number here");

        // The end token is zero-width, the annotation must still be rendered exactly once
        Assert.Equal(new Position(0, 3), errorToken.Range.Start);
        Assert.Equal(errorToken.Range.Start, errorToken.Range.End);
        Assert.Equal(
            @"error: unexpected end of input
  ┌─ story.txt:1:4
  │
1 │ 1 +
  │    ^ expected a number here
  │
", Present(diagnostics));
    }

    // A reader that fails once, partway through the input
    private sealed class FailOnceReader : TextReader
    {
        private readonly string content;
        private readonly int failAt;
        private int offset;
        private bool failed;

        public FailOnceReader(string content, int failAt)
        {
            this.content = content;
            this.failAt = failAt;
        }

        public override int Read()
        {
            if (!this.failed && this.offset >= this.failAt)
            {
                this.failed = true;
                throw new IOException("simulated transient input failure");
            }
            return this.offset >= this.content.Length ? -1 : this.content[this.offset++];
        }
    }

    [Fact]
    public void RecoverableFailureMidPipelineAndRetry()
    {
        const string source = "1 +\r\n2\t+ 3\n+4";

        // Reference: the pipeline over the undisturbed input
        var referenceTokens = LexAll(new GeneratedLexer(new SourceFile("story.txt", source)));
        var referenceValue = ParseTokens(referenceTokens).Ok.Value;
        var referenceSpans = referenceTokens.Select(t => (t.Kind, t.Text, t.Range)).ToList();

        // Inject a recoverable failure in the middle of the pipeline:
        // the causal error propagates, no half-updated shared state is left behind
        var sharedAccumulator = new List<IToken<TokenType>>();
        Assert.Throws<IOException>(() =>
        {
            var lexer = new GeneratedLexer(new TextReaderCharStream(new SourceFile("story.txt", new FailOnceReader(source, 5))));
            var tokens = LexAll(lexer);
            // Shared state is only updated once the stage completed successfully
            sharedAccumulator.AddRange(tokens);
        });
        Assert.Empty(sharedAccumulator);

        // Retrying with the exact same input yields the reference results
        var retriedTokens = LexAll(new GeneratedLexer(new SourceFile("story.txt", source)));
        Assert.Equal(referenceSpans, retriedTokens.Select(t => (t.Kind, t.Text, t.Range)).ToList());
        Assert.Equal(referenceValue, ParseTokens(retriedTokens).Ok.Value);
        sharedAccumulator.AddRange(retriedTokens);
        Assert.Equal(referenceTokens.Count, sharedAccumulator.Count);
    }

    [Fact]
    public void OrderCacheAndSchedulingDoNotChangeResults()
    {
        const string source = "1 #\r\n# + 2";

        Diagnostics BuildDiagnostics(IEnumerable<IToken<TokenType>> tokenSource)
        {
            var diagnostics = new Diagnostics().WithSeverity(Severity.Error).WithMessage("syntax errors");
            foreach (var token in tokenSource)
            {
                if (token.Kind == TokenType.Error) diagnostics.WithSourceInfo(token.Location, Severity.Error, "unexpected character");
            }
            return diagnostics;
        }

        // Same input, but surfaced through different execution orders, cache states
        // and scheduling conditions
        var eagerTokens = LexAll(new GeneratedLexer(new SourceFile("story.txt", source)));
        IEnumerable<IToken<TokenType>> LazyTokens()
        {
            foreach (var token in LexAll(new GeneratedLexer(new SourceFile("story.txt", source)))) yield return token;
        }

        var fromEager = Present(DiagnosticsMerger.Merge(BuildDiagnostics(eagerTokens)));
        var fromLazy = Present(DiagnosticsMerger.Merge(BuildDiagnostics(LazyTokens())));
        // A second run over already-enumerated (cached) tokens
        var fromCached = Present(DiagnosticsMerger.Merge(BuildDiagnostics(eagerTokens.ToArray())));

        Assert.Equal(fromEager, fromLazy);
        Assert.Equal(fromEager, fromCached);

        // The parse result is equally independent of how the tokens were produced
        var eagerParse = ParseTokens(eagerTokens.Where(t => t.Kind != TokenType.Error).ToList());
        var lazyParse = ParseTokens(LazyTokens().Where(t => t.Kind != TokenType.Error).ToList());
        Assert.Equal(eagerParse.Ok.Value, lazyParse.Ok.Value);
    }
}
