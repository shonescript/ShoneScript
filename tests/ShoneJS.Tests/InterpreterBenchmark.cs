// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using ShoneJS;

namespace ShoneJS.Tests;

public static class InterpreterBenchmark
{
    // Run explicitly with a debugger/REPL or call from a temporary harness; timings are informational only.
    public static (double ExpressionsPerSecond, double CallsPerSecond) Measure(int iterations = 100_000)
    {
        var expressionEngine = new Engine();
        var expressionWatch = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) expressionEngine.Evaluate("1 + 2 * 3");
        expressionWatch.Stop();

        var callEngine = new Engine();
        callEngine.Evaluate("function add(a, b) { return a + b; }");
        var callWatch = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) callEngine.Evaluate("add(20, 22)");
        callWatch.Stop();
        return (iterations / expressionWatch.Elapsed.TotalSeconds, iterations / callWatch.Elapsed.TotalSeconds);
    }
}
