// Copyright (c) 2021-2022 Yoakke.
// Licensed under the Apache License, Version 2.0.
// Source repository: https://github.com/LanguageDev/Yoakke

using Yoakke.Streams;
using Yoakke.SynKit.Lexer;
using Yoakke.SynKit.Text;

namespace Yoakke.SynKit.SpanGuard.Tests;

/// <summary>
/// A hand-written lexer over <see cref="TextReaderCharStream"/> that is token-compatible with
/// the generated <see cref="GuardLexer"/>. Both must produce identical source spans.
/// </summary>
internal sealed class HandwrittenGuardLexer : ILexer<Token<GuardTokenKind>>
{
    private readonly ICharStream chars;

    public Position Position => this.chars.Position;

    public bool IsEnd => this.chars.IsEnd;

    public HandwrittenGuardLexer(ISourceFile sourceFile)
    {
        this.chars = new TextReaderCharStream(sourceFile);
    }

    public Token<GuardTokenKind> Next()
    {
    begin:
        if (this.IsEnd) return this.chars.ConsumeToken(GuardTokenKind.End, 0);
        var peek = this.chars.Peek('\0');
        if (peek is ' ' or '\t' or '\r' or '\n')
        {
            this.chars.Consume();
            goto begin;
        }
        if (peek == '+') return this.chars.ConsumeToken(GuardTokenKind.Plus, 1);
        if (char.IsLetter(peek) || peek == '_')
        {
            var length = 1;
            while (true)
            {
                var ahead = this.chars.LookAhead(length, '\0');
                if (!char.IsLetterOrDigit(ahead) && ahead != '_') break;
                ++length;
            }
            return this.chars.ConsumeToken(GuardTokenKind.Identifier, length);
        }
        if (char.IsDigit(peek))
        {
            var length = 1;
            while (char.IsDigit(this.chars.LookAhead(length, '\0'))) ++length;
            return this.chars.ConsumeToken(GuardTokenKind.Number, length);
        }
        return this.chars.ConsumeToken(GuardTokenKind.Error, 1);
    }
}
