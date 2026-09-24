// Copyright (c) 2021-2022 Yoakke.
// Licensed under the Apache License, Version 2.0.
// Source repository: https://github.com/LanguageDev/Yoakke

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Yoakke.SynKit.Lexer;
using Yoakke.SynKit.Parser;
using Yoakke.SynKit.Reporting;
using Yoakke.SynKit.Reporting.Present;
using Yoakke.SynKit.Text;

namespace Yoakke.SynKit.SpanGuard.Tests;

/// <summary>
/// The end-to-end acceptance story for the unified source span, line mapping and diagnostic
/// merging boundary. Everything enters through the public API of the generated lexer, the
/// generated parser and the diagnostics presenter; no internal objects are fabricated.
/// </summary>
public class UnifiedPipelineAcceptanceTests
{
    // The fixed batch of inputs the story runs on
    private static readonly (string Path, string Text)[] Batch =
    [
        ("ok.src", "1+2\r\n+3"),   // mixed newlines inside ignored whitespace, parses to 6
        ("tab.src", "4\t+\t5"),    // tabs between tokens, parses to 9
        ("eof.src", "7+"),         // error at the zero-width end-of-input position
    ];

    private sealed record TokenSpan(GuardTokenKind Kind, string Text, Range Range);

    private sealed record PipelineOutput(
        IReadOnlyList<TokenSpan> Tokens,
        int? Value,
        string DiagnosticsText);

    // Stage 1: lexing through the generated lexer (public entry)
    private static List<Token<GuardTokenKind>> LexAll(ISourceFile file)
    {
        var lexer = new GuardLexer(new TextReaderCharStream(file));
        var tokens = new List<Token<GuardTokenKind>>();
        while (true)
        {
            var token = lexer.Next();
            tokens.Add(token);
            if (token.Kind == GuardTokenKind.End) break;
        }
        return tokens;
    }

    // Stage 2: parsing through the generated parser (public entry)
    private static ParseResult<int> Parse(IEnumerable<Token<GuardTokenKind>> tokens) =>
        new GuardParser(tokens).ParseTopExpression();

    // Stage 3: diagnostics merging and rendering through the presenter (public entry)
    private static Diagnostics ToDiagnostics(ParseError error, ISourceFile file)
    {
        var position = (Position)error.Position;
        var diag = new Diagnostics()
            .WithSeverity(Severity.Error)
            .WithCode("E_PARSE")
            .WithMessage("syntax error");
        foreach (var element in error.Elements.Values)
        {
            var expected = string.Join(", ", element.Expected.Select(e =>
                e is GuardTokenKind kind ? kind.ToString() : $"'{e}'"));
            // The error position becomes a zero-width annotation
            diag.WithSourceInfo(
                new Location(file, new Range(position, position)),
                Severity.Error,
                $"expected {expected} in {element.Context}");
        }
        return diag;
    }

    private static PipelineOutput FinishPipeline(
        ISourceFile file,
        List<Token<GuardTokenKind>> tokens,
        TextDiagnosticsPresenter? presenter,
        StringWriter? writer)
    {
        var result = Parse(tokens);
        int? value = null;
        var diagnosticsText = string.Empty;
        if (result.IsOk)
        {
            // Diagnostic information attached to a success must not change the result
            value = result.Ok.Value;
        }
        else
        {
            presenter ??= new TextDiagnosticsPresenter(writer = new StringWriter());
            // Only the text this Present call appends belongs to this file's output
            var before = writer!.ToString().Length;
            presenter.Present(ToDiagnostics(result.Error, file));
            diagnosticsText = writer.ToString().Substring(before).Replace("\r\n", "\n");
        }
        return new(
            tokens.Select(t => new TokenSpan(t.Kind, t.Text, t.Range)).ToList(),
            value,
            diagnosticsText);
    }

    private static PipelineOutput RunPipeline(ISourceFile file) =>
        FinishPipeline(file, LexAll(file), presenter: null, writer: null);

    private static void AssertOutputEqual(PipelineOutput expected, PipelineOutput actual)
    {
        Assert.Equal(expected.Tokens, actual.Tokens);
        Assert.Equal(expected.Value, actual.Value);
        Assert.Equal(expected.DiagnosticsText, actual.DiagnosticsText);
    }

    [Fact]
    public void Batch_EndToEnd_RecordsCallerVisibleResultsAndInvariants()
    {
        var outputs = Batch.ToDictionary(
            entry => entry.Path,
            entry => RunPipeline(new SourceFile(entry.Path, entry.Text)));

        // ok.src: caller sees the parsed value; tokens prove mixed newlines map to one table
        var ok = outputs["ok.src"];
        Assert.Equal(6, ok.Value);
        Assert.Equal(
        [
            new TokenSpan(GuardTokenKind.Number, "1", new Range(new Position(0, 0), 1)),
            new TokenSpan(GuardTokenKind.Plus, "+", new Range(new Position(0, 1), 1)),
            new TokenSpan(GuardTokenKind.Number, "2", new Range(new Position(0, 2), 1)),
            new TokenSpan(GuardTokenKind.Plus, "+", new Range(new Position(1, 0), 1)),
            new TokenSpan(GuardTokenKind.Number, "3", new Range(new Position(1, 1), 1)),
            new TokenSpan(GuardTokenKind.End, string.Empty, new Range(new Position(1, 2), 0)),
        ], ok.Tokens);

        // tab.src: a tab is exactly one column in every span
        var tab = outputs["tab.src"];
        Assert.Equal(9, tab.Value);
        Assert.Equal(
        [
            new TokenSpan(GuardTokenKind.Number, "4", new Range(new Position(0, 0), 1)),
            new TokenSpan(GuardTokenKind.Plus, "+", new Range(new Position(0, 2), 1)),
            new TokenSpan(GuardTokenKind.Number, "5", new Range(new Position(0, 4), 1)),
            new TokenSpan(GuardTokenKind.End, string.Empty, new Range(new Position(0, 5), 0)),
        ], tab.Tokens);

        // eof.src: caller sees a syntax error at the zero-width end-of-input position
        var eof = outputs["eof.src"];
        Assert.Null(eof.Value);
        Assert.Equal(new TokenSpan(GuardTokenKind.End, string.Empty, new Range(new Position(0, 2), 0)), eof.Tokens[^1]);
        Assert.Equal(
@"error[E_PARSE]: syntax error
  ┌─ eof.src:1:3
  │
1 │ 7+
  │   ^ expected expression in expression
  │
", eof.DiagnosticsText);
    }

    [Fact]
    public void RecoverableFailure_MidPipeline_LeavesNoHalfUpdatedState_RetryMatchesControl()
    {
        const string text = "1+2\r\n+3";
        var control = RunPipeline(new SourceFile("ok.src", text));
        var controlFile = new SourceFile("ok.src", text);

        // Inject a recoverable failure in the middle of lexing (the 7th read lands on the
        // last line, after the first line was already committed)
        var exception = new IOException("simulated transient read failure");
        var reader = new FailingTextReader(text, exception, failAtCall: 7);
        var file = new SourceFile("ok.src", reader);
        var lexer = new GuardLexer(new TextReaderCharStream(file));

        // The tokens before the failure arrive normally
        Assert.Equal(GuardTokenKind.Number, lexer.Next().Kind);
        Assert.Equal(GuardTokenKind.Plus, lexer.Next().Kind);
        Assert.Equal(GuardTokenKind.Number, lexer.Next().Kind);
        // The failure surfaces from the public lexer entry with its causal error intact
        var thrown = Record.Exception(() => lexer.Next());
        Assert.Same(exception, thrown);
        Assert.Equal(1, reader.FailureCount);

        // No half-updated shared state: the committed line table is exactly the first line
        Assert.Equal(1, file.AvailableLines);
        Assert.Equal("1+2\r\n", file.GetLine(0));

        // Retrying the identical input over the same shared state matches the control run
        var retried = RunPipeline(file);
        AssertOutputEqual(control, retried);

        // The success carries no leftover caches, counters or temporary state from the
        // failed call: the line table is indistinguishable from a never-failing file
        Assert.Equal(controlFile.AvailableLines, file.AvailableLines);
        for (var i = 0; i < controlFile.AvailableLines; ++i) Assert.Equal(controlFile.GetLine(i), file.GetLine(i));
    }

    [Fact]
    public void ReorderedSchedules_AndSharedPresenter_YieldIdenticalResults()
    {
        // Schedule 1: file by file, forward order, a fresh presenter per file
        var perFile = Batch.ToDictionary(
            entry => entry.Path,
            entry => RunPipeline(new SourceFile(entry.Path, entry.Text)));

        // Schedule 2: reverse order, all lexing first, then parse + present through one
        // shared presenter instance (shared mutable state across calls)
        var files = Batch.Reverse()
            .Select(entry => (entry.Path, File: (ISourceFile)new SourceFile(entry.Path, entry.Text)))
            .ToList();
        var lexed = files.Select(f => (f.Path, f.File, Tokens: LexAll(f.File))).ToList();
        var sharedWriter = new StringWriter();
        var sharedPresenter = new TextDiagnosticsPresenter(sharedWriter);
        var scheduled = new Dictionary<string, PipelineOutput>();
        foreach (var (path, file, tokens) in lexed)
        {
            scheduled[path] = FinishPipeline(file, tokens, sharedPresenter, sharedWriter);
        }

        foreach (var (path, _) in Batch) AssertOutputEqual(perFile[path], scheduled[path]);
    }
}
