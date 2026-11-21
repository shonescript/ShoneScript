// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneJs;
using ShoneJs.Parsing;
using ShoneJs.Runtime;
using ShoneLisp;
using ShoneLisp.Parsing;
using ShoneLisp.Runtime;
using ShoneLua;
using ShonePy;
using ShoneProlog;

const string usage = """
    Usage: ShoneScript [--ts] [--bigfloat] [-e <script>]
           ShoneScript --py [-e <script>]
           ShoneScript --lisp [-e <script>]
           ShoneScript --lua [-e <script>]
           ShoneScript --prolog [-e <query>] [<file>]
           ShoneScript -h | --help
    Pipe a complete script to stdin or use the JavaScript prompt with no arguments.
    """;

var language = args is ["--py" or "--lisp" or "--lua" or "--prolog", ..] ? args[0] : null;
if (language is not null) args = args[1..];

bool typeScript = false, enableBigFloat = false;
if (language is null)
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

if (language is not null)
{
    if (language == "--prolog")
    {
        if (args is ["-e", var query]) return EvaluateProlog(query, "<command>");
        if (args.Length == 0 && Console.IsInputRedirected)
            return EvaluateProlog(Console.In.ReadToEnd(), "<stdin>");
        if (args is [var file])
        {
            try { return EvaluateProlog(File.ReadAllText(file), file); }
            catch (IOException error) { Console.Error.WriteLine($"{file}: {error.Message}"); return 1; }
            catch (UnauthorizedAccessException error) { Console.Error.WriteLine($"{file}: {error.Message}"); return 1; }
        }
    }
    else
    {
        if (args is ["-e", var script]) return EvaluateScript(language, script, "<command>");
        if (args.Length == 0 && Console.IsInputRedirected)
            return EvaluateScript(language, Console.In.ReadToEnd(), "<stdin>");
    }
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

static int EvaluateProlog(string source, string fileName)
{
    try
    {
        var engine = new PrologEngine();
        var result = engine.Evaluate(source, fileName);
        if (!result.Succeeded) { Console.WriteLine("false."); return 1; }
        foreach (var solution in result.Solutions)
            Console.WriteLine(solution.Bindings.Count == 0 ? "true." : string.Join(", ", solution.Bindings.Select(binding => $"{binding.Key} = {binding.Value}")) + ".");
        return 0;
    }
    catch (PrologParseException error)
    {
        return ReportDiagnostic(error.FileName ?? fileName, error.Position, "SyntaxError", error.Message);
    }
    catch (PrologRuntimeException error)
    {
        Console.Error.WriteLine($"{fileName}: RuntimeError: {error.Message}");
        return 1;
    }
}

static int EvaluateScript(string language, string source, string fileName)
{
    try
    {
        switch (language)
        {
            case "--lisp":
                var lisp = new LispEngine(output: Console.Out).Execute(source, fileName);
                if (!lisp.IsUnspecified) Console.WriteLine(lisp);
                return 0;
            case "--lua":
                var lua = new LuaEngine(output: Console.Out).Execute(source, fileName);
                if (lua.HasReturnValue) Console.WriteLine(lua.Value);
                return 0;
            default:
                return EvaluatePython(source, fileName);
        }
    }
    catch (LispParseException error)
    {
        return ReportDiagnostic(error.FileName ?? fileName, error.Position, "SyntaxError", error.Message);
    }
    catch (LuaParseException error)
    {
        return ReportDiagnostic(error.FileName ?? fileName, error.Position, "SyntaxError", error.Message);
    }
    catch (LispRuntimeException error)
    {
        return ReportDiagnostic(error.FileName ?? fileName, error.Position, "RuntimeError", error.Message);
    }
    catch (LuaRuntimeException error)
    {
        return ReportDiagnostic(error.FileName ?? fileName, error.Position, "RuntimeError", error.Message);
    }
}

static int ReportDiagnostic(string fileName, SourcePosition position, string kind, string message)
{
    Console.Error.WriteLine($"{fileName}:{position.Line}:{position.Column}: {kind}: {message}");
    return 1;
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
