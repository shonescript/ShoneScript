using ShoneJS;
using ShoneJS.Parsing;
using ShoneJS.Runtime;
using ShoneJS.TypeScript;

const string usage = "Usage: ShoneJS.Cli [--ts] [-e <script>]\nPipe a complete script to stdin or use the JavaScript prompt with no arguments.\nTypeScript mode requires SHONEJS_TSGO to point to the native checker.";

// --ts is a leading mode switch; remaining arguments select expression, redirected input, or the prompt.
var typeScript = args.Length > 0 && args[0] == "--ts";
if (typeScript) args = args[1..];

if (args is ["-h" or "--help"])
{
    Console.WriteLine(usage);
    return 0;
}

var engine = new Engine(new EngineOptions
{
    TypeScript = new TypeScriptOptions { ToolPath = Environment.GetEnvironmentVariable("SHONEJS_TSGO") }
});

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

Console.WriteLine("ShoneJS. Enter one script per line. Type .exit to quit.");
while (true)
{
    Console.Write("js> ");
    var line = Console.ReadLine();
    if (line is null || line.Trim() == ".exit")
        return 0;
    if (!string.IsNullOrWhiteSpace(line))
        Evaluate(engine, line);
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
    catch (TypeScriptCheckException error)
    {
        foreach (var diagnostic in error.Diagnostics)
            Console.Error.WriteLine(diagnostic);
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
