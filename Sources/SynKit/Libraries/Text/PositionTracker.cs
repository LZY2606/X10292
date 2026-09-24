// Copyright (c) 2021-2022 Yoakke.
// Licensed under the Apache License, Version 2.0.
// Source repository: https://github.com/LanguageDev/Yoakke

namespace Yoakke.SynKit.Text;

/// <summary>
/// Tracks a <see cref="Text.Position"/> while text is consumed character by character.
/// This is the single, shared definition of how consumed characters map to line and column
/// advancement, used by both handwritten and generated lexers so that no layer re-interprets
/// newlines or empty ranges on its own.
/// </summary>
/// <remarks>
/// The units of the tracked <see cref="Text.Position"/> are defined as follows:
/// <list type="bullet">
/// <item>Columns are counted in UTF-16 code units (<see cref="char"/>s), not Unicode scalar
/// values. A character outside the Basic Multilingual Plane - which is a surrogate pair in
/// UTF-16 - occupies 2 columns. Consumers that work in Unicode scalar values must account for
/// this difference themselves.</item>
/// <item>A Windows-style line terminator CRLF (<c>"\r\n"</c>) advances exactly one line.</item>
/// <item>A lone CR (<c>'\r'</c>) or LF (<c>'\n'</c>) advances exactly one line.</item>
/// <item>A horizontal tab (<c>'\t'</c>) advances exactly one column. Expanding tabs to a tab-stop
/// width is a presentation concern and does not belong here.</item>
/// <item>Any other control character (<see cref="char.IsControl(char)"/>) is zero-width and
/// advances nothing.</item>
/// <item>Any other character advances exactly one column.</item>
/// </list>
/// </remarks>
public sealed class PositionTracker
{
    /// <summary>
    /// The current <see cref="Text.Position"/> the tracker is at.
    /// </summary>
    public Position Position { get; private set; }

    // The previously consumed character, needed to collapse CRLF into a single newline
    private char previousChar;
    private bool hasPreviousChar;

    /// <summary>
    /// Initializes a new instance of the <see cref="PositionTracker"/> class
    /// at the origin <see cref="Text.Position"/> (line 0, column 0).
    /// </summary>
    public PositionTracker()
        : this(default)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PositionTracker"/> class
    /// at the given <see cref="Text.Position"/>.
    /// </summary>
    /// <param name="position">The <see cref="Text.Position"/> to start tracking from.</param>
    public PositionTracker(Position position)
    {
        this.Position = position;
    }

    /// <summary>
    /// Resets the tracker to the origin <see cref="Text.Position"/> (line 0, column 0),
    /// forgetting any previously consumed character.
    /// </summary>
    public void Reset() => this.Reset(default);

    /// <summary>
    /// Resets the tracker to the given <see cref="Text.Position"/>,
    /// forgetting any previously consumed character.
    /// </summary>
    /// <param name="position">The <see cref="Text.Position"/> to reset to.</param>
    public void Reset(Position position)
    {
        this.Position = position;
        this.previousChar = default;
        this.hasPreviousChar = false;
    }

    /// <summary>
    /// Advances the tracked <see cref="Text.Position"/> by consuming a single character.
    /// </summary>
    /// <param name="current">The consumed character.</param>
    /// <returns>The new <see cref="Text.Position"/> after the consumption.</returns>
    public Position Consume(char current)
    {
        this.Position = Advance(this.Position, this.previousChar, this.hasPreviousChar, current);
        this.previousChar = current;
        this.hasPreviousChar = true;
        return this.Position;
    }

    /// <summary>
    /// Computes the <see cref="Text.Position"/> after consuming a single character, using the
    /// same units as <see cref="PositionTracker"/>.
    /// </summary>
    /// <param name="position">The <see cref="Text.Position"/> before the consumption.</param>
    /// <param name="previous">The previously consumed character, if any.</param>
    /// <param name="current">The character being consumed.</param>
    /// <returns>The <see cref="Text.Position"/> after the consumption.</returns>
    public static Position Advance(Position position, char? previous, char current) =>
        Advance(position, previous ?? default, previous.HasValue, current);

    private static Position Advance(Position position, char previous, bool hasPrevious, char current)
    {
        // Windows-style line terminator: the line was already advanced at the '\r'
        if (hasPrevious && previous == '\r' && current == '\n') return position;
        // Lone CR or LF both terminate a line
        if (current == '\r' || current == '\n') return position.Newline();
        // A horizontal tab occupies a single column, display expansion is a presentation concern
        if (current == '\t') return position.Advance();
        // Any other control character is zero-width
        if (char.IsControl(current)) return position;
        // Anything else occupies a single UTF-16 code unit column
        return position.Advance();
    }
}
