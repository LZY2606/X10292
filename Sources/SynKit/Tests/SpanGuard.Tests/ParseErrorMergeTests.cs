// Copyright (c) 2021-2022 Yoakke.
// Licensed under the Apache License, Version 2.0.
// Source repository: https://github.com/LanguageDev/Yoakke

using System.Linq;
using Xunit;
using Yoakke.SynKit.Parser;
using Yoakke.SynKit.Text;

namespace Yoakke.SynKit.SpanGuard.Tests;

/// <summary>
/// Guards for the parser-side diagnostic merging boundary: merged parse errors keep a stable
/// expectation order, stay free of duplicates, and the public position types and formats stay
/// compatible.
/// </summary>
public class ParseErrorMergeTests
{
    [Fact]
    public void MergedExpectations_EnumerateInStableOrder_WithoutDuplicates()
    {
        ParseError Merge() =>
            (new ParseError("^", null, 12, "expression")
            | new ParseError("|", null, 12, "expression")
            | new ParseError(":", null, 12, "expression")
            | new ParseError("^", null, 12, "expression"))!; // duplicate expectation
        var first = Merge();
        var second = Merge();
        // Deterministic order, duplicates merged away
        Assert.Equal(new object[] { "^", "|", ":" }, first.Elements["expression"].Expected.ToArray());
        Assert.Equal(
            first.Elements["expression"].Expected.ToArray(),
            second.Elements["expression"].Expected.ToArray());
    }

    [Fact]
    public void Merge_PrefersFurthestPosition_RegardlessOfOperandOrder()
    {
        ParseError Far() => new("x", null, 12, "ctx-far");
        ParseError Near() => new("y", null, 3, "ctx-near");
        var first = Far() | Near();
        var second = Near() | Far();
        foreach (var result in new[] { first, second })
        {
            Assert.NotNull(result);
            Assert.Equal(12, result.Position);
            Assert.True(result.Elements.ContainsKey("ctx-far"));
            Assert.False(result.Elements.ContainsKey("ctx-near"));
        }
    }

    [Fact]
    public void Merge_IsAssociative_InContextsAndExpectations()
    {
        ParseError A() => new("^", null, 12, "expression");
        ParseError B() => new("|", null, 12, "expression");
        ParseError C() => new(":", null, 12, "other");
        var left = (A() | B()) | C();
        var right = A() | (B() | C());
        Assert.NotNull(left);
        Assert.NotNull(right);
        Assert.Equal(left.Elements.Keys.ToArray(), right.Elements.Keys.ToArray());
        foreach (var key in left.Elements.Keys)
        {
            Assert.Equal(
                left.Elements[key].Expected.ToArray(),
                right.Elements[key].Expected.ToArray());
        }
    }

    [Fact]
    public void ZeroWidthEofError_PublicPositionFormatStaysCompatible()
    {
        // A parse error at the end of input carries the zero-width end position
        var error = new ParseError("Number", null, new Position(2, 5), "expression");
        Assert.Equal(new Position(2, 5), error.Position);
        Assert.Equal("line 3, column 6", error.Position.ToString());
    }

    [Fact]
    public void AttachedErrorInfo_DoesNotChangeTheSuccessResult()
    {
        // A successful parse may carry diagnostic information about how far alternatives got;
        // inspecting or attaching it must not change the success value
        ParseResult<int> result = ParseResult.Ok(42, 3, new ParseError("x", null, 1, "ctx"));
        Assert.True(result.IsOk);
        Assert.NotNull(result.FurthestError);
        Assert.Equal(42, result.Ok.Value);
        Assert.Equal(3, result.Ok.Offset);
    }
}
