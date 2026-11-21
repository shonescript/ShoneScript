// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using BenchmarkDotNet.Attributes;
using ShoneLua;
using ShonePy;

namespace ShoneScript.Benchmarks;

[MemoryDiagnoser]
public class LuaPythonBenchmarks
{
    private LuaEngine _lua = null!;
    private ParsedLuaScript _luaScript = null!;
    private PythonEngine _python = null!;
    private ParsedPythonScript _pythonScript = null!;

    [GlobalSetup]
    public void Setup()
    {
        _lua = new LuaEngine();
        _luaScript = _lua.ParseScript("""
            local total = 0
            for i = 1, 1000 do
                total = total + i * i
            end
            return total
            """);
        _python = new PythonEngine();
        _pythonScript = _python.ParseScript("""
            total = 0
            for i in range(1, 1001):
                total += i * i
            total
            """);

        if (LuaCpuLoop().AsNumber() != 333_833_500 || !PythonCpuLoop().Equals(PyValue.From(333_833_500)))
            throw new InvalidOperationException("A Lua or Python benchmark returned an unexpected result.");
    }

    [Benchmark]
    public LuaValue LuaCpuLoop() => _lua.Execute(_luaScript).Value;

    [Benchmark]
    public PyValue PythonCpuLoop() => _python.Execute(_pythonScript);
}
