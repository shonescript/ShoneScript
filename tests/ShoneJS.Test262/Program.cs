// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using ShoneScript.Parsing;
using ShoneScript.Runtime;

namespace ShoneScript.Test262;

internal static class Program
{
    // Parent and worker communicate success through an exact stdout token; diagnostics stay on stderr.
    private const string SuccessToken = "TEST262_PASS";
    private const string AsyncComplete = "Test262:AsyncTestComplete";
    private const string AsyncFailure = "Test262:AsyncTestFailure:";
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(5);

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine("Usage: dotnet run --project tests/ShoneJs.Test262 [--list | <pinned test path> [--mode sloppy|strict|raw]]");
            Console.WriteLine("Executes the pinned Test262 script smoke suite with the pinned upstream harness.");
            Console.WriteLine($"Each variant runs in a fresh worker with a {WorkerTimeout.TotalSeconds:g}-second timeout.");
            return 0;
        }

        try
        {
            var catalog = TestCatalog.Load(AppContext.BaseDirectory);
            if (args is ["--worker", var path, var modeName])
            {
                var test = catalog.ReadTest(path);
                Mode mode = modeName switch
                {
                    nameof(Mode.Sloppy) => Mode.Sloppy,
                    nameof(Mode.Strict) => Mode.Strict,
                    nameof(Mode.Raw) => Mode.Raw,
                    _ => throw new ArgumentException($"Unknown test mode '{modeName}'.")
                };
                if (!test.Modes.Contains(mode)) throw new ArgumentException($"Mode {mode} is not enabled for '{path}'.");
                ExecuteVariant(catalog, test, mode);
                Console.WriteLine(SuccessToken);
                return 0;
            }

            if (args is ["--list"])
            {
                foreach (string testPath in catalog.Paths) Console.WriteLine(testPath);
                return 0;
            }

            Mode? requestedMode = null;
            string[] paths;
            if (args is [var testPathArgument, "--mode", var modeArgument])
            {
                requestedMode = modeArgument switch
                {
                    "sloppy" => Mode.Sloppy,
                    "strict" => Mode.Strict,
                    "raw" => Mode.Raw,
                    _ => throw new ArgumentException($"Unknown test mode '{modeArgument}'.")
                };
                paths = [testPathArgument];
            }
            else
            {
                if (args.Length > 1) throw new ArgumentException("Use --help for usage.");
                paths = args.Length == 0 ? catalog.Paths.ToArray() : args;
            }
            Console.WriteLine($"Test262 smoke revision {catalog.Revision} (pinned upstream harness)");
            int passed = 0, failed = 0;
            foreach (string testPath in paths)
            {
                TestCase test;
                try { test = catalog.ReadTest(testPath); }
                catch (Exception error)
                {
                    Console.WriteLine($"ERROR {testPath} — {error.Message}");
                    failed++;
                    continue;
                }

                if (requestedMode is { } selected && !test.Modes.Contains(selected))
                {
                    Console.WriteLine($"ERROR {testPath} — mode {selected} is not enabled");
                    failed++;
                    continue;
                }

                foreach (Mode variant in test.Modes.Where(mode => requestedMode is null || mode == requestedMode))
                {
                    string? failure;
                    try { failure = await RunWorker(testPath, variant); }
                    catch (Exception error) { failure = $"worker infrastructure error: {error.Message}"; }
                    string name = $"{testPath} [{variant.ToString().ToLowerInvariant()}]";
                    if (failure is null)
                    {
                        Console.WriteLine($"PASS {name}");
                        passed++;
                    }
                    else
                    {
                        Console.WriteLine($"FAIL {name} — {failure}");
                        failed++;
                    }
                }
            }

            Console.WriteLine($"{passed} passed, {failed} failed");
            return failed == 0 ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    // Isolate each variant so globals cannot leak, and enforce a wall-clock bound outside the JS engine.
    private static async Task<string?> RunWorker(string path, Mode mode)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? throw new InvalidOperationException("Unable to resolve worker executable."),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--worker");
        start.ArgumentList.Add(path);
        start.ArgumentList.Add(mode.ToString());

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start worker.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(), stdout, stderr).WaitAsync(WorkerTimeout);
        }
        catch (TimeoutException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            return $"worker exceeded {WorkerTimeout.TotalSeconds:g} seconds";
        }

        string output = await stdout;
        string errors = await stderr;
        if (process.ExitCode == 0 && output == SuccessToken + Environment.NewLine && errors.Length == 0) return null;
        string details = (errors.Length > 0 ? errors : output).Trim();
        if (details.Length > 1500) details = details[..1500] + "…";
        return $"worker exited {process.ExitCode}: {(details.Length > 0 ? details : "missing result")}";
    }

    private static void ExecuteVariant(TestCatalog catalog, TestCase test, Mode mode)
    {
        // Wall-clock enforcement belongs to the parent process, including for loops and recursion.
        var engine = new Engine(new EngineOptions { MaximumLoopIterations = int.MaxValue });
        ParsedScript script;
        try { script = engine.ParseScript(test.Source, forceStrict: mode == Mode.Strict); }
        catch (JsSyntaxException error)
        {
            if (test.Negative is { Phase: "parse", Type: "SyntaxError" }) return;
            throw new InvalidOperationException($"Unexpected parse SyntaxError: {error.Message}", error);
        }

        if (test.Negative is { Phase: "parse" } parseNegative)
            throw new InvalidOperationException($"Expected {parseNegative.Type} during parse, but parsing succeeded.");
        if (mode == Mode.Strict && !script.IsStrict)
            throw new InvalidOperationException("The strict parse option did not produce a strict script.");

        int completions = 0;
        string? asyncFailure = null;
        // The pinned async harness reports completion or failure through print; drain queued jobs afterward.
        if (test.IsAsync)
        {
            engine.SetGlobal("print", JsValue.From(new JsFunction(args =>
            {
                string message = string.Join(" ", args.Select(arg => arg.ToJsString()));
                if (message == AsyncComplete) completions++;
                else if (message.StartsWith(AsyncFailure, StringComparison.Ordinal)) asyncFailure ??= message;
                return JsValue.Undefined;
            })));
        }

        // Raw tests intentionally bypass all harness setup and include scripts.
        if (mode != Mode.Raw)
        {
            try
            {
                foreach (string standard in new[] { "harness/sta.js", "harness/assert.js" })
                    engine.Execute(engine.ParseScript(catalog.ReadHarness(standard)));
                if (test.IsAsync)
                    engine.Execute(engine.ParseScript(catalog.ReadHarness(TestCatalog.AsyncHarness)));
                foreach (string include in test.Includes)
                    engine.Execute(engine.ParseScript(catalog.ReadHarness("harness/" + include)));
            }
            catch (Exception error)
            {
                throw new InvalidOperationException($"Harness setup failed: {error.Message}", error);
            }
        }

        try
        {
            engine.Execute(script);
            if (test.IsAsync) engine.DrainJobs();
        }
        catch (JsException error)
        {
            if (test.Negative is { Phase: "runtime" } runtimeNegative && error.ErrorName == runtimeNegative.Type) return;
            throw new InvalidOperationException($"Unexpected runtime {error.ErrorName ?? "thrown value"}: {error.Message}", error);
        }

        if (test.Negative is { Phase: "runtime" } expected)
            throw new InvalidOperationException($"Expected {expected.Type} during execution, but execution succeeded.");
        if (asyncFailure is not null)
            throw new InvalidOperationException(asyncFailure);
        if (test.IsAsync && completions != 1)
            throw new InvalidOperationException($"Expected exactly one {AsyncComplete} signal, but received {completions}.");
    }
}
