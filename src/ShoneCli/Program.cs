// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneJs;
using ShoneJs.Parsing;
using ShoneJs.Runtime;
using ShonePy;

const string usage = "Usage: ShoneScript [--ts] [--bigfloat] [-e <script>]\n       ShoneScript --py [-e <script>]\nPipe a complete script to stdin or use the JavaScript prompt with no arguments.";

var python = args is ["--py", ..];
if (python) args = args[1..];

bool typeScript = false, enableBigFloat = false;
if (!python)
{
    int optionCount = 0;
    while (optionCount < args.Length)
    {
        if (!typeScript && args[optionCount] == "--ts") typeScript = true;
        else if (!enableBigFloat && args[optionCount] == "--bigfloat") enableBigFloat = true;
        else break;
        optionCount++;
    }
    args = args[optionCount..];
}

if (args is ["-h" or "--help"])
{
    Console.WriteLine(usage);
    return 0;
}

if (python)
{
    if (args is ["-e", var pythonSource]) return EvaluatePython(pythonSource, "<command>");
    if (args.Length == 0 && Console.IsInputRedirected) return EvaluatePython(Console.In.ReadToEnd(), "<stdin>");
    Console.Error.WriteLine(usage);
    return 2;
}

var engine = new Engine(new EngineOptions { EnableBigFloat = enableBigFloat });

if (args is ["-e", var source])
    return Evaluate(engine, source, typeScript);

if (args.Length != 0)
{
    Console.Error.WriteLine(usage);
    return 2;
}

if (Console.IsInputRedirected)
    return Evaluate(engine, Console.In.ReadToEnd(), typeScript);

if (typeScript)
{
    Console.Error.WriteLine(usage);
    return 2;
}

Console.WriteLine("ShoneJs. Enter one script per line. Type .exit to quit.");
while (true)
{
    Console.Write("js> ");
    var line = Console.ReadLine();
    if (line is null || line.Trim() == ".exit")
        return 0;
    if (!string.IsNullOrWhiteSpace(line))
        Evaluate(engine, line);
}

static int EvaluatePython(string source, string fileName)
{
    try
    {
        var engine = new PythonEngine(new PythonOptions { Output = Console.Out });
        var result = engine.Evaluate(source, fileName);
        if (result.Type != PyType.None) Console.WriteLine(result);
        return 0;
    }
    catch (PythonException error)
    {
        var location = error.Line > 0 ? $"{error.FileName ?? fileName}:{error.Line}:{error.Column}: " : "";
        Console.Error.WriteLine($"{location}{error.ErrorName}: {error.Message}");
        return 1;
    }
}

// Keep script evaluation and job draining together so promise jobs finish before the result is printed.
static int Evaluate(Engine engine, string source, bool typeScript = false)
{
    try
    {
        var result = typeScript ? engine.EvaluateTypeScript(source) : engine.Evaluate(source);
        engine.DrainJobs();
        Console.WriteLine(result.ToString());
        return 0;
    }
    catch (JsSyntaxException error)
    {
        Console.Error.WriteLine($"SyntaxError: {error.Message}");
    }
    catch (JsException error)
    {
        Console.Error.WriteLine(error.ErrorName is { } name
            ? $"{name}: {error.Message}"
            : $"Uncaught: {error.Message}");
    }
    catch (InvalidOperationException error)
    {
        Console.Error.WriteLine(error.Message);
    }

    return 1;
}
