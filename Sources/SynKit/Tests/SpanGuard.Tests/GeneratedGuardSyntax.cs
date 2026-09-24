// Copyright (c) 2021-2022 Yoakke.
// Licensed under the Apache License, Version 2.0.
// Source repository: https://github.com/LanguageDev/Yoakke

using Yoakke.SynKit.Lexer;
using Yoakke.SynKit.Lexer.Attributes;
using Yoakke.SynKit.Parser.Attributes;
using IgnoreAttribute = Yoakke.SynKit.Lexer.Attributes.IgnoreAttribute;

namespace Yoakke.SynKit.SpanGuard.Tests;

/// <summary>
/// The token kind shared by the hand-written and the generated guard lexers and the generated
/// guard parser. All of them must observe identical source spans for identical input.
/// </summary>
public enum GuardTokenKind
{
    [End] End,
    [Error] Error,
    [Ignore] [Regex(Regexes.Whitespace)] Whitespace,
    [Regex(Regexes.Identifier)] Identifier,
    [Regex(Regexes.IntLiteral)] Number,
    [Token("+")] Plus,
}

/// <summary>
/// The generated lexer under test. It is the generated counterpart of
/// <see cref="HandwrittenGuardLexer"/> and must produce the exact same token spans.
/// </summary>
[Lexer(typeof(GuardTokenKind))]
internal partial class GuardLexer
{
}

/// <summary>
/// The generated parser under test: a minimal left-associative sum grammar.
/// </summary>
[Parser(typeof(GuardTokenKind))]
internal partial class GuardParser
{
    [Rule("top_expression : expression End")]
    private static int TopLevel(int n, IToken _) => n;

    [Left("+")]
    [Rule("expression")]
    private static int Op(int left, IToken op, int right) => left + right;

    [Rule("expression : Number")]
    private static int Number(IToken tok) => int.Parse(tok.Text);
}
