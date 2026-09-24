// Copyright (c) 2021-2022 Yoakke.
// Licensed under the Apache License, Version 2.0.
// Source repository: https://github.com/LanguageDev/Yoakke

using System;

namespace Yoakke.SynKit.Text;

/// <summary>
/// Extensions for <see cref="ISourceFile"/> that provide the single, shared mapping between
/// linear character offsets and 2D <see cref="Text.Position"/>s, so that no consumer has to
/// re-interpret line terminators on its own.
/// </summary>
/// <remarks>
/// The mapping works in UTF-16 code units (<see cref="char"/>s), exactly like
/// <see cref="PositionTracker"/>. Line terminators are counted as they are stored by the
/// <see cref="ISourceFile"/>: each line returned by <see cref="ISourceFile.GetLine(int)"/>
/// includes its terminator, so the offset of the first character of line <c>n + 1</c> is the
/// accumulated length of all lines up to and including line <c>n</c>.
/// </remarks>
public static class SourceFileExtensions
{
    /// <summary>
    /// Maps a linear character offset to a 2D <see cref="Text.Position"/> inside the source file.
    /// </summary>
    /// <param name="sourceFile">The <see cref="ISourceFile"/> to map in.</param>
    /// <param name="offset">The 0-based character offset to map. The offset of the end of the
    /// available text maps to the end of the last available line.</param>
    /// <returns>The <see cref="Text.Position"/> corresponding to <paramref name="offset"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown, when <paramref name="offset"/> is
    /// negative, or points beyond the available text of <paramref name="sourceFile"/>.</exception>
    public static Position GetPosition(this ISourceFile sourceFile, int offset)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), offset, "The offset cannot be negative");

        var lineStart = 0;
        for (var line = 0; line < sourceFile.AvailableLines; ++line)
        {
            var lineLength = sourceFile.GetLine(line).Length;
            if (offset < lineStart + lineLength || line == sourceFile.AvailableLines - 1)
            {
                if (offset > lineStart + lineLength)
                {
                    throw new ArgumentOutOfRangeException(nameof(offset), offset, "The offset is beyond the available text");
                }
                return new Position(line, offset - lineStart);
            }
            lineStart += lineLength;
        }
        throw new ArgumentOutOfRangeException(nameof(offset), offset, "The offset is beyond the available text");
    }

    /// <summary>
    /// Maps a 2D <see cref="Text.Position"/> to a linear character offset inside the source file.
    /// </summary>
    /// <param name="sourceFile">The <see cref="ISourceFile"/> to map in.</param>
    /// <param name="position">The <see cref="Text.Position"/> to map.</param>
    /// <returns>The 0-based character offset corresponding to <paramref name="position"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown, when <paramref name="position"/>
    /// has negative components, or points outside of the available text of
    /// <paramref name="sourceFile"/>.</exception>
    public static int GetOffset(this ISourceFile sourceFile, Position position)
    {
        if (position.Line < 0) throw new ArgumentOutOfRangeException(nameof(position), position, "The line cannot be negative");
        if (position.Column < 0) throw new ArgumentOutOfRangeException(nameof(position), position, "The column cannot be negative");
        if (position.Line >= sourceFile.AvailableLines)
        {
            throw new ArgumentOutOfRangeException(nameof(position), position, "The line is beyond the available text");
        }

        var offset = 0;
        for (var line = 0; line < position.Line; ++line) offset += sourceFile.GetLine(line).Length;
        if (position.Column > sourceFile.GetLine(position.Line).Length)
        {
            throw new ArgumentOutOfRangeException(nameof(position), position, "The column is beyond the available text");
        }
        return offset + position.Column;
    }
}
