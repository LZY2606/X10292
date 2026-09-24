// Copyright (c) 2021-2022 Yoakke.
// Licensed under the Apache License, Version 2.0.
// Source repository: https://github.com/LanguageDev/Yoakke

using System;
using System.Collections.Generic;
using Yoakke.SynKit.Text;
using Range = Yoakke.SynKit.Text.Range;

namespace Yoakke.SynKit.Reporting;

/// <summary>
/// Provides the single, shared way of merging multiple <see cref="Diagnostics"/> into one,
/// so that no consumer re-implements ordering or deduplication on its own.
/// </summary>
/// <remarks>
/// The merge follows these rules:
/// <list type="bullet">
/// <item>The merged head takes the highest <see cref="Reporting.Severity"/> among the inputs,
/// and the first non-null code and message in input order.</item>
/// <item>Attached information keeps a stable order: informations of earlier inputs come first,
/// and within one input the original order is preserved.</item>
/// <item>Structurally identical informations are deduplicated, keeping the first occurrence.
/// Two source informations are identical, if they refer to the same file path, the same
/// <see cref="Range"/>, and carry the same severity and message. Two footnotes are identical,
/// if they carry the same severity and message. This also covers the zero-width
/// <see cref="Range"/>s that error recovery tends to produce repeatedly at the same
/// position.</item>
/// <item>The inputs are never mutated, and the result does not share mutable state with
/// them.</item>
/// </list>
/// </remarks>
public static class DiagnosticsMerger
{
    /// <summary>
    /// Merges multiple <see cref="Diagnostics"/> into a single one.
    /// </summary>
    /// <param name="diagnostics">The <see cref="Diagnostics"/> to merge, in order.</param>
    /// <returns>A new <see cref="Diagnostics"/> representing all of the inputs.</returns>
    public static Diagnostics Merge(params Diagnostics[] diagnostics) => Merge((IEnumerable<Diagnostics>)diagnostics);

    /// <summary>
    /// Merges multiple <see cref="Diagnostics"/> into a single one.
    /// </summary>
    /// <param name="diagnostics">The <see cref="Diagnostics"/> to merge, in order.</param>
    /// <returns>A new <see cref="Diagnostics"/> representing all of the inputs.</returns>
    public static Diagnostics Merge(IEnumerable<Diagnostics> diagnostics)
    {
        var result = new Diagnostics();
        var seenInfos = new HashSet<InfoKey>();
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic is null) continue;
            // Head: highest severity, first non-null code and message
            if (diagnostic.Severity != null && (result.Severity == null || diagnostic.Severity > result.Severity))
            {
                result.Severity = diagnostic.Severity;
            }
            if (result.Code == null) result.Code = diagnostic.Code;
            if (result.Message == null) result.Message = diagnostic.Message;
            // Information: stable order, structural deduplication
            foreach (var info in diagnostic.Information)
            {
                if (seenInfos.Add(InfoKey.Of(info))) result.Information.Add(info);
            }
        }
        return result;
    }

    // A structural identity for IDiagnosticInfo instances
    private readonly struct InfoKey : IEquatable<InfoKey>
    {
        public static InfoKey Of(IDiagnosticInfo info) => info switch
        {
            SourceDiagnosticInfo source => new(
                source.Location.File?.Path,
                source.Location.Range,
                source.Severity,
                source.Message),
            _ => new(null, null, info.Severity, info.Message),
        };

        private readonly string? path;
        private readonly Range? range;
        private readonly Severity? severity;
        private readonly string? message;

        private InfoKey(string? path, Range? range, Severity? severity, string? message)
        {
            this.path = path;
            this.range = range;
            this.severity = severity;
            this.message = message;
        }

        public bool Equals(InfoKey other) =>
               this.path == other.path
            && this.range == other.range
            && this.severity == other.severity
            && this.message == other.message;

        public override bool Equals(object? obj) => obj is InfoKey other && this.Equals(other);

        public override int GetHashCode() => (this.path, this.range, this.severity, this.message).GetHashCode();
    }
}
