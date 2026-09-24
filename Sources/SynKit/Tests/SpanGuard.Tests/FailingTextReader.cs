// Copyright (c) 2021-2022 Yoakke.
// Licensed under the Apache License, Version 2.0.
// Source repository: https://github.com/LanguageDev/Yoakke

using System;
using System.IO;

namespace Yoakke.SynKit.SpanGuard.Tests;

/// <summary>
/// A <see cref="TextReader"/> over a fixed string that throws a scheduled exception exactly once
/// at a given read call, without consuming any input. After the failure the reader behaves
/// normally, so the failed operation can simply be retried.
/// </summary>
public sealed class FailingTextReader : TextReader
{
    private readonly StringReader inner;
    private readonly Exception scheduled;
    private readonly int failAtCall;

    /// <summary>
    /// The total number of <see cref="Read"/> calls so far.
    /// </summary>
    public int ReadCalls { get; private set; }

    /// <summary>
    /// The number of times the scheduled exception was thrown.
    /// </summary>
    public int FailureCount { get; private set; }

    public FailingTextReader(string text, Exception scheduled, int failAtCall)
    {
        this.inner = new StringReader(text);
        this.scheduled = scheduled;
        this.failAtCall = failAtCall;
    }

    public override int Read()
    {
        ++this.ReadCalls;
        if (this.ReadCalls == this.failAtCall)
        {
            ++this.FailureCount;
            throw this.scheduled;
        }
        return this.inner.Read();
    }

    public override int Peek() => this.inner.Peek();
}

/// <summary>
/// Simulates a resource budget being exhausted mid-read.
/// </summary>
public sealed class ResourceBudgetExceededException : Exception
{
    public ResourceBudgetExceededException(string message)
        : base(message)
    {
    }
}
