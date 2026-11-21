// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using ShoneJs;

namespace ShoneJs.Tests;

public static class InterpreterBenchmark
{
    // Run explicitly with a debugger/REPL or call from a temporary harness; timings are informational only.
    public static (double ExpressionsPerSecond, double CallsPerSecond) Measure(int iterations = 100_000)
    {
        var expressionEngine = new JsEngine();
        var expressionWatch = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) expressionEngine.Evaluate("1 + 2 * 3");
        expressionWatch.Stop();

        var callEngine = new JsEngine();
        callEngine.Evaluate("function add(a, b) { return a + b; }");
        var callWatch = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) callEngine.Evaluate("add(20, 22)");
        callWatch.Stop();
        return (iterations / expressionWatch.Elapsed.TotalSeconds, iterations / callWatch.Elapsed.TotalSeconds);
    }
}
