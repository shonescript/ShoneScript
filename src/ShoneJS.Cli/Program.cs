using ShoneJS;
using ShoneJS.Parsing;
using ShoneJS.Runtime;

const string usage = "Usage: ShoneJS.Cli [-e <script>]\nWith no arguments, enter one script per line or pipe a complete script to stdin.";

if (args is ["-h" or "--help"])
{
    Console.WriteLine(usage);
    return 0;
}

if (args is ["-e", var source])
    return Evaluate(new Engine(), source);

if (args.Length != 0)
{
    Console.Error.WriteLine(usage);
    return 2;
}

var engine = new Engine();
if (Console.IsInputRedirected)
    return Evaluate(engine, Console.In.ReadToEnd());

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

static int Evaluate(Engine engine, string source)
{
    try
    {
        var result = engine.Evaluate(source);
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
