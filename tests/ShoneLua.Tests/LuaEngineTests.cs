// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneJs.Parsing;
using ShoneLua;

namespace ShoneLua.Tests;

public sealed class LuaEngineTests
{
    [Theory]
    [InlineData("return 2 + 3 * 4 ^ 2", "50")]
    [InlineData("return -2 ^ 2 ^ 3", "-256")]
    [InlineData("return 2 ^ -3", "0.125")]
    [InlineData("return (-7 // 3) .. ':' .. (-7 % 3) .. ':' .. (7 % -3)", "-3:2:-2")]
    [InlineData("return 1 + 2 .. 3 * 4", "312")]
    [InlineData("return 0x1.8p2 + .5 + 1.5e1 + ' 0x2 '", "23.5")]
    [InlineData("return 0x1.00000000000008p0 == 1 and 0x1.00000000000008001p0 > 1", "true")]
    [InlineData("return 0x1p-1075 == 0 and 0x1.0000000000001p-1075 > 0", "true")]
    [InlineData("return 9007199254740992 == 9007199254740993", "true")]
    [InlineData("return 0 / 0 ~= 0 / 0 and not (0 / 0 < 1)", "true")]
    [InlineData("return 'a' < 'b' and 2 <= 2 and 2 >= 1 and 4 > 3 and 1 ~= '1'", "true")]
    [InlineData("return false and missing() or 0 and '' or missing()", "")]
    [InlineData("return nil or false and missing() or 'fallback'", "fallback")]
    [InlineData("return not nil and not false and not not 0 and not not ''", "true")]
    public void OperatorsReturnExpectedValues(string source, string expected)
    {
        var result = new LuaEngine().Evaluate(source);
        Assert.True(result.HasReturnValue);
        Assert.Equal(expected, result.Value.ToString());
    }

    [Fact]
    public void ReadsCommentsAndShortAndLongStrings()
    {
        const string source = "-- line\r\n--[=[ ignored ]]\r\nlong comment ]=]\n" +
            "return [==[\r\nline one\nline two]==] .. '\\n\\x41\\066\\u{1F600}\\z \n C'";
        Assert.Equal("line one\nline two\nAB😀C", new LuaEngine().Execute(source).Value.AsString());
    }

    [Fact]
    public void DifferentiatesExplicitNilFromReturningNoValue()
    {
        var lua = new LuaEngine();
        Assert.Equal(new LuaExecutionResult(true, LuaValue.Nil), lua.Execute("return nil"));
        Assert.Equal(new LuaExecutionResult(true, LuaValue.Nil), lua.Execute("return unknownGlobal"));
        Assert.Equal(new LuaExecutionResult(false, LuaValue.Nil), lua.Execute("return;"));
        Assert.Equal(new LuaExecutionResult(false, LuaValue.Nil), lua.Execute("local x = 1"));
        Assert.Equal(new LuaExecutionResult(false, LuaValue.Nil), lua.Execute(""));
    }

    [Fact]
    public void ReturnListsEvaluateEachExpressionButExposeOnlyTheFirstValue()
    {
        using var output = new StringWriter { NewLine = "\n" };
        var engine = new LuaEngine(output: output);
        var result = engine.Execute("local function f() return nil, print('later') end; return f()");
        Assert.Equal(new LuaExecutionResult(true, LuaValue.Nil), result);
        Assert.Equal("later\n", output.ToString());
    }

    [Fact]
    public void ChunksUseSameGlobalsButFreshLocalsAndIndependentEngineState()
    {
        var engine = new LuaEngine();
        var other = new LuaEngine();
        engine.SetGlobal("counter", LuaValue.FromNumber(10));
        var script = engine.ParseScript("counter = (counter or 0) + 1; local hidden = 9; return counter", "counter.lua");
        Assert.Equal(11, engine.Execute(script).Value.AsNumber());
        Assert.Equal(12, engine.Execute(script).Value.AsNumber());
        Assert.Equal(1, other.Execute(script).Value.AsNumber());
        Assert.Equal(12, engine.GetGlobal("counter").AsNumber());
        Assert.Equal("counter.lua", script.FileName);
        Assert.Equal(LuaValue.Nil, engine.Execute("return hidden").Value);
        engine.Execute("counter = nil");
        Assert.Equal(LuaValue.Nil, engine.GetGlobal("counter"));
    }

    [Fact]
    public void ClosuresCaptureTheBindingsVisibleWhenDefinedAndCanUpdateThem()
    {
        var engine = new LuaEngine();
        var result = engine.Execute("""
            x = 1
            local readGlobal = function() return x end
            local x = x + 1
            local function make(start)
                local n = start
                return function(by)
                    n = n + by
                    return n
                end
            end
            local inc = make(x)
            local other = make(10)
            do
                local x = 20
                save = function() return x + inc(1) end
            end
            local x = 100
            return readGlobal() .. ',' .. save() .. ',' .. inc(5) .. ',' .. other(1) .. ',' .. x
            """);
        Assert.Equal("1,23,8,11,100", result.Value.AsString());
        Assert.Equal(29, engine.Execute("return save()").Value.AsNumber());
    }

    [Fact]
    public void LocalFunctionsCanRecurseAndNamedFunctionsCanTargetTables()
    {
        var result = new LuaEngine().Execute("""
            local t = {}
            local function fact(n)
                if n < 0 then return nil elseif n <= 1 then return 1 else return n * fact(n - 1) end
            end
            function t.compute(a, b)
                if b == nil then return fact(a) end
            end
            return t.compute(5)
            """);
        Assert.Equal(120, result.Value.AsNumber());
    }

    [Fact]
    public void WhileAndNumericForUseBlockLocalsAndPropagateReturn()
    {
        var result = new LuaEngine().Execute("""
            local sum, i = 0, 0
            while i < 4 do
                local inc = 2
                i = i + 1
                if i == 3 then break end
                sum = sum + inc
            end
            local fs = {}
            for i = 5, 1, -2 do
                fs[i] = function() return i end
            end
            for j = 1, 2 do
                return sum .. ',' .. i .. ',' .. fs[1]() .. ',' .. fs[3]() .. ',' .. fs[5]() .. ',' .. (inc or 'nil')
            end
            """);
        Assert.Equal("4,3,1,3,5,nil", result.Value.AsString());
    }

    [Fact]
    public void TablesKeepIdentityUseTypedKeysAndDeleteNilFields()
    {
        var result = new LuaEngine().Execute("""
            local t = {10, 20; label = 'item', [false] = 'flag', ['1'] = 'string', }
            local alias = t
            local key = {}
            alias[key] = 'identity'
            local f = function() end
            alias[f] = 'function'
            alias[2] = nil
            return (t == alias and t ~= {}) and
                (t[1] .. ',' .. t['1'] .. ',' .. t.label .. ',' .. t[false] .. ',' .. t[key] .. ',' .. t[f] .. ',' .. #t .. ',' .. (t[2] or 'gone'))
            """);
        Assert.Equal("10,string,item,flag,identity,function,1,gone", result.Value.AsString());
    }

    [Fact]
    public void AssignmentEvaluatesRightHandValuesAndIndexAddressesBeforeStores()
    {
        var result = new LuaEngine().Execute("""
            local i, t = 1, {10, 20}
            t[1], t[2] = t[2], t[1]
            i, t[i] = 2, 70
            local a, b, c = 1, 2
            a, b, c = b, a
            return i .. ',' .. t[1] .. ',' .. t[2] .. ',' .. a .. ',' .. b .. ',' .. (c or 'nil')
            """);
        Assert.Equal("2,70,10,2,1,nil", result.Value.AsString());
    }

    [Fact]
    public void PrintWritesTabSeparatedValuesAndDoesNotReturnAChunkValue()
    {
        using var output = new StringWriter { NewLine = "\n" };
        var engine = new LuaEngine(output: output);
        Assert.False(engine.Execute("print('hi', 3.5, false, nil); print [[ok]]; print()").HasReturnValue);
        Assert.Equal("hi\t3.5\tfalse\tnil\nok\n\n", output.ToString());
    }

    [Theory]
    [InlineData("local x = 1\r\n@", 2, 1)]
    [InlineData("return 1e+", 1, 8)]
    [InlineData("return 'bad\\q'", 1, 8)]
    [InlineData("local x = 0\nif x then\n", 3, 1)]
    [InlineData("while true do\nlocal function f() break end\nend", 2, 20)]
    public void ParseErrorsHaveSourcePositions(string source, int line, int column)
    {
        var error = Assert.Throws<LuaParseException>(() => new LuaEngine().ParseScript(source, "bad.lua"));
        Assert.Equal("bad.lua", error.FileName);
        Assert.Equal(line, error.Position.Line);
        Assert.Equal(column, error.Position.Column);
        Assert.Equal(error.Span.Start, error.Position);
    }

    [Fact]
    public void RuntimeErrorsInStoredClosuresUseTheirDefinitionFile()
    {
        var engine = new LuaEngine();
        engine.Execute("function f()\n  return unknown.field\nend", "definition.lua");
        var error = Assert.Throws<LuaRuntimeException>(() => engine.Execute("return f()", "invoke.lua"));
        Assert.Equal("definition.lua", error.FileName);
        Assert.Equal(new SourcePosition(22, 2, 10), error.Position);
        Assert.Equal(error.Span.Start, error.Position);
        Assert.Contains("index", error.Message);
    }

    [Theory]
    [InlineData("local t = {}; t[nil] = 1", "nil")]
    [InlineData("local t = {}; t[0/0] = 1", "NaN")]
    [InlineData("for i=1, 2, 0 do end", "zero")]
    [InlineData("return nil + 1", "number")]
    [InlineData("return missing()", "non-function")]
    public void RuntimeTypeErrorsAreLuaDiagnostics(string source, string expected)
    {
        var error = Assert.Throws<LuaRuntimeException>(() => new LuaEngine().Execute(source));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void ExecutionBudgetIsRenewedAfterFailureAndSuccess()
    {
        var engine = new LuaEngine(new LuaOptions { MaximumSteps = 12 });
        var chunk = engine.ParseScript("return 2 + 3");
        for (var i = 0; i < 5; i++) Assert.Equal(5, engine.Execute(chunk).Value.AsNumber());
        var error = Assert.Throws<LuaRuntimeException>(() => engine.Execute("while true do end", "budget.lua"));
        Assert.Contains("steps", error.Message);
        Assert.Equal("budget.lua", error.FileName);
        Assert.Equal(5, engine.Execute(chunk).Value.AsNumber());
        Assert.Contains("steps", Assert.Throws<LuaRuntimeException>(() => engine.Execute("for i = 1, 100 do end")).Message);
        Assert.Equal(5, engine.Execute(chunk).Value.AsNumber());
    }

    [Fact]
    public void RejectsNonPositiveStepBudget()
    {
        var engine = new LuaEngine(new LuaOptions { MaximumSteps = 0 });
        Assert.Throws<ArgumentOutOfRangeException>("MaximumSteps", () => engine.Execute(""));
    }

    [Fact]
    public void ParserAndEvaluatorLimitNestedAndFlatExpressions()
    {
        var engine = new LuaEngine(new LuaOptions { MaximumParseDepth = 8, MaximumEvaluationDepth = 8 });
        var nested = "return " + new string('(', 100) + "1" + new string(')', 100);
        Assert.Contains("parse depth", Assert.Throws<LuaParseException>(() => engine.ParseScript(nested)).Message);
        var nestedBlocks = string.Concat(Enumerable.Repeat("do ", 100)) + string.Concat(Enumerable.Repeat("end ", 100));
        Assert.Contains("parse depth", Assert.Throws<LuaParseException>(() => engine.ParseScript(nestedBlocks)).Message);
        var flat = "return 1" + string.Concat(Enumerable.Repeat("+1", 100));
        var postfix = "local t = {}; return t" + string.Concat(Enumerable.Repeat(".key", 100));
        Assert.Contains("evaluation depth", Assert.Throws<LuaRuntimeException>(() => engine.Execute(engine.ParseScript(flat))).Message);
        Assert.Contains("evaluation depth", Assert.Throws<LuaRuntimeException>(() => engine.Execute(engine.ParseScript(postfix))).Message);
        Assert.Equal(1, engine.Execute("return 1").Value.AsNumber());
    }

    [Fact]
    public void RecursionIsBoundedAndDepthIsRenewedAfterFailure()
    {
        var engine = new LuaEngine(new LuaOptions { MaximumCallDepth = 5 });
        engine.Execute("function f() return f() end");
        Assert.Contains("call depth", Assert.Throws<LuaRuntimeException>(() => engine.Execute("return f()")).Message);
        Assert.Equal(1, engine.Execute("return (function() return 1 end)()").Value.AsNumber());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    public void RejectsDepthConfigurationOutsideSafeRange(int depth)
    {
        var engine = new LuaEngine(new LuaOptions { MaximumParseDepth = depth });
        Assert.Throws<ArgumentOutOfRangeException>("MaximumParseDepth", () => engine.ParseScript(""));
        engine.Options.MaximumParseDepth = 128;
        engine.Options.MaximumEvaluationDepth = depth;
        Assert.Throws<ArgumentOutOfRangeException>("MaximumEvaluationDepth", () => engine.Execute(""));
        engine.Options.MaximumEvaluationDepth = 128;
        engine.Options.MaximumCallDepth = depth;
        Assert.Throws<ArgumentOutOfRangeException>("MaximumCallDepth", () => engine.Execute(""));
    }
}
