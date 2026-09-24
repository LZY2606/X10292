// Copyright (c) 2021-2022 Yoakke.
// Licensed under the Apache License, Version 2.0.
// Source repository: https://github.com/LanguageDev/Yoakke

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Generic.Polyfill;
using System.Linq;
using System.Net.Sockets;
using System.Text;

namespace Yoakke.SynKit.Parser;

// A set of expected inputs that preserves first-insertion order, so that merged parse
// errors enumerate their expectations in a stable, deterministic order.
internal class ParseErrorExpectationSet : ISet<object>
{
    private readonly List<object> elements = new();

    public ParseErrorExpectationSet(ISet<object> expected)
    {
        this.UnionWith(expected);
    }

    public ParseErrorExpectationSet(object expected)
    {
        this.elements.Add(expected);
    }

    private ParseErrorExpectationSet()
    {
    }

    public int Count => this.elements.Count;

    public bool IsReadOnly => true;

    public bool Add(object item)
    {
        if (this.Contains(item)) return false;
        this.elements.Add(item);
        return true;
    }
    public void Clear()
    {
        this.elements.Clear();
    }
    public bool Contains(object item) => this.elements.Contains(item);
    public void CopyTo(object[] array, int arrayIndex) => this.elements.CopyTo(array, arrayIndex);
    public void ExceptWith(IEnumerable<object> other) => throw new NotImplementedException();
    public IEnumerator<object> GetEnumerator() => this.elements.GetEnumerator();
    public void IntersectWith(IEnumerable<object> other) => throw new NotImplementedException();
    public bool IsProperSubsetOf(IEnumerable<object> other) => throw new NotImplementedException();
    public bool IsProperSupersetOf(IEnumerable<object> other) => throw new NotImplementedException();
    public bool IsSubsetOf(IEnumerable<object> other) => throw new NotImplementedException();
    public bool IsSupersetOf(IEnumerable<object> other) => throw new NotImplementedException();
    public bool Overlaps(IEnumerable<object> other) => throw new NotImplementedException();
    public bool Remove(object item) => throw new NotImplementedException();
    public bool SetEquals(IEnumerable<object> other) => throw new NotImplementedException();
    public void SymmetricExceptWith(IEnumerable<object> other) => throw new NotImplementedException();
    public void UnionWith(IEnumerable<object> other)
    {
        foreach (var item in other) this.Add(item);
    }

    internal ParseErrorExpectationSet Merge(ParseErrorExpectationSet other)
    {
        var newExpected = new ParseErrorExpectationSet();
        newExpected.UnionWith(this);
        newExpected.UnionWith(other);
        return newExpected;
    }
    void ICollection<object>.Add(object item) => throw new NotImplementedException();
    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
}
