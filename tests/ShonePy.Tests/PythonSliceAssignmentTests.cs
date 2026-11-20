// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Tests;

public sealed class PythonSliceAssignmentTests
{
    [Fact]
    public void MutatesTheOriginalListAndUsesTheOriginalContentsForSelfAssignment()
    {
        var result = new PythonEngine().Evaluate("""
            a = [0, 1, 2, 3]
            b = a
            a[2:1] = iter([4, 5])
            a[::-1] = a
            a[1:3] += (6,)
            (a is b, b)
            """);

        Assert.Equal("(True, [3, 2, 5, 6, 4, 1, 0])", result.Repr());
    }

    [Fact]
    public void LengthMismatchLeavesTheListUnchanged()
    {
        var engine = new PythonEngine();
        var result = engine.Evaluate("""
            a = [1, 2, 3]
            try: a[::2 ** 80] = [7, 8]
            except ValueError: pass
            a
            """);

        Assert.Equal("[1, 2, 3]", result.Repr());
    }

    [Fact]
    public void RecomputesBoundsWhenAnIteratorCallbackShrinksTheList()
    {
        var result = new PythonEngine().Evaluate("""
            from itertools import accumulate
            a = [0, 1, 2, 3]
            def shrink(x, y):
                a[:] = []
                return y
            a[::1] = accumulate([8, 9], shrink)
            a
            """);

        Assert.Equal("[8, 9]", result.Repr());
    }

    [Fact]
    public void DoesNotCommitAReplacementWhenAnIteratorExhaustsTheBudget()
    {
        var engine = new PythonEngine(new PythonOptions { MaximumSteps = 50 });
        var error = Assert.Throws<PythonException>(() => engine.Evaluate("""
            from itertools import count
            a = [1]
            a[:] = count()
            """));

        Assert.Equal("maximum execution steps exceeded", error.Message);
        Assert.Equal("[1]", engine.GetGlobal("a").Repr());
    }
}
