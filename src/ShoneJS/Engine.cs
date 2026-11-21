// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneJs.Parsing;
using ShoneJs.Runtime;

namespace ShoneJs;

/// <summary>Configures features and execution limits for an engine.</summary>
public sealed class EngineOptions
{
    /// <summary>Sets the iteration budget used by script execution and Promise job draining.</summary>
    public int MaximumLoopIterations { get; set; } = 1_000_000;

    /// <summary>Maximum interpreter steps per host execution or job checkpoint.</summary>
    public int MaximumExecutionSteps { get; set; } = 10_000_000;

    /// <summary>Maximum queued microtasks and reactions on one pending Promise.</summary>
    public int MaximumPendingJobs { get; set; } = 100_000;

    /// <summary>Maximum source length in UTF-16 code units.</summary>
    public int MaximumSourceLength { get; set; } = 4_000_000;

    /// <summary>Lifetime cancellation for parsing, execution and Promise jobs.</summary>
    public CancellationToken CancellationToken { get; set; }

    /// <summary>Installs the BigFloat global when the engine is created.</summary>
    public bool EnableBigFloat { get; set; }
}

/// <summary>A parsed JavaScript script that can be passed to <see cref="Engine.Execute"/>.</summary>
public sealed class ParsedScript
{
    internal ParsedScript(List<Stmt> statements, bool strict)
    {
        Statements = statements;
        IsStrict = strict;
    }

    internal List<Stmt> Statements { get; }
    /// <summary>Indicates whether the script was parsed in strict mode.</summary>
    public bool IsStrict { get; }
}

/// <summary>A parsed ECMAScript module and its static dependencies in source order.</summary>
public sealed class ParsedModule
{
    internal ParsedModule(List<Stmt> statements, List<ModuleImport> imports, List<ModuleExport> exports, List<string> requests)
    {
        Statements = statements;
        Imports = imports;
        Exports = exports;
        RequestedModules = requests.AsReadOnly();
    }

    internal List<Stmt> Statements { get; }
    internal List<ModuleImport> Imports { get; }
    internal List<ModuleExport> Exports { get; }
    public IReadOnlyList<string> RequestedModules { get; }
}

/// <summary>A parsed TypeScript script ready for direct execution.</summary>
public sealed class CheckedTypeScript
{
    internal CheckedTypeScript(ParsedScript script) => Script = script;
    internal ParsedScript Script { get; }
}

/// <summary>Parses and interprets JavaScript and supported TypeScript scripts.</summary>
/// <remarks>Executions on an instance share globals and pending Promise jobs. Use a separate instance for independent sessions.</remarks>
public sealed class Engine
{
    private readonly Interpreter _interpreter;
    /// <summary>Gets the engine configuration.</summary>
    public EngineOptions Options { get; }
    /// <summary>Creates an engine with a fresh global environment.</summary>
    public Engine(EngineOptions? options = null)
    {
        Options = options ?? new EngineOptions();
        _interpreter = new Interpreter(Options.EnableBigFloat);
    }

    /// <summary>Parses JavaScript without executing it.</summary>
    public ParsedScript ParseScript(string source) => ParseScript(source, cancellationToken: default);

    public ParsedScript ParseScript(string source, CancellationToken cancellationToken) => ParseScript(source, forceStrict: false, cancellationToken);

    /// <summary>Parses JavaScript with an option to enable strict mode without altering source positions.</summary>
    public ParsedScript ParseScript(string source, bool forceStrict) => ParseScript(source, forceStrict, default);

    public ParsedScript ParseScript(string source, bool forceStrict, CancellationToken cancellationToken)
    {
        using var linked = LinkParsingCancellation(source, cancellationToken);
        var token = linked?.Token ?? (cancellationToken.CanBeCanceled ? cancellationToken : Options.CancellationToken);
        var parser = new Parser(new Lexer(source, token), forceStrict: forceStrict, cancellationToken: token);
        var statements = parser.Parse();
        return new ParsedScript(statements, parser.IsStrict);
    }

    /// <summary>Executes a parsed script and returns its completion value.</summary>
    public JsValue Execute(ParsedScript script) => Execute(script, default);

    public JsValue Execute(ParsedScript script, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(script);
        ConfigureExecution();
        return _interpreter.RunGuarded(() => _interpreter.Execute(script.Statements, script.IsStrict), cancellationToken);
    }

    /// <summary>Parses and executes JavaScript in the current global environment.</summary>
    public JsValue Evaluate(string source) => Evaluate(source, default);

    public JsValue Evaluate(string source, CancellationToken cancellationToken) =>
        Execute(ParseScript(source, cancellationToken), cancellationToken);

    /// <summary>Parses a strict module without linking or executing it.</summary>
    public ParsedModule ParseModule(string source, CancellationToken cancellationToken = default)
    {
        using var linked = LinkParsingCancellation(source, cancellationToken);
        var token = linked?.Token ?? (cancellationToken.CanBeCanceled ? cancellationToken : Options.CancellationToken);
        return new Parser(new Lexer(source, token), ParseGoal.Module, cancellationToken: token).ParseModule();
    }

    /// <summary>Registers a module on the engine owner thread before evaluating its graph.</summary>
    public void RegisterModule(Uri url, ParsedModule module, IReadOnlyDictionary<string, Uri>? resolutions = null) =>
        _interpreter.RegisterModule(url, module, resolutions);

    /// <summary>Links and evaluates a registered graph, returning a Promise for its module namespace.</summary>
    public JsValue EvaluateModule(Uri url, CancellationToken cancellationToken = default)
    {
        ConfigureExecution();
        return _interpreter.RunGuarded(() => _interpreter.EvaluateModule(url), cancellationToken);
    }

    private CancellationTokenSource? LinkParsingCancellation(string source, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source);
        token.ThrowIfCancellationRequested();
        var lifetime = Options.CancellationToken;
        lifetime.ThrowIfCancellationRequested();
        if (source.Length > Options.MaximumSourceLength) throw new InvalidOperationException("Source length limit exceeded");
        return token.CanBeCanceled && lifetime.CanBeCanceled && token != lifetime
            ? CancellationTokenSource.CreateLinkedTokenSource(token, lifetime) : null;
    }

    private void ConfigureExecution()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(Options.MaximumLoopIterations);
        ArgumentOutOfRangeException.ThrowIfNegative(Options.MaximumExecutionSteps);
        ArgumentOutOfRangeException.ThrowIfNegative(Options.MaximumPendingJobs);
        _interpreter.MaximumLoopIterations = Options.MaximumLoopIterations;
        _interpreter.MaximumExecutionSteps = Options.MaximumExecutionSteps;
        _interpreter.MaximumPendingJobs = Options.MaximumPendingJobs;
        _interpreter.CancellationToken = Options.CancellationToken;
    }

    /// <summary>Parses supported TypeScript syntax without executing it.</summary>
    /// <exception cref="JsSyntaxException">The source could not be parsed by ShoneJs.</exception>
    public CheckedTypeScript ParseTypeScript(string source)
    {
        using var linked = LinkParsingCancellation(source, default);
        var token = Options.CancellationToken;
        var parser = new Parser(new Lexer(source, token), typeScript: true, cancellationToken: token);
        var statements = parser.ParseTypeScript();
        return new CheckedTypeScript(new ParsedScript(statements, parser.IsStrict));
    }

    /// <summary>Interprets a previously parsed TypeScript script directly.</summary>
    public JsValue ExecuteTypeScript(CheckedTypeScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return Execute(script.Script);
    }

    /// <summary>Parses and executes supported TypeScript syntax directly in the current global environment.</summary>
    public JsValue EvaluateTypeScript(string source) => ExecuteTypeScript(ParseTypeScript(source));

    /// <summary>Runs the pending Promise jobs. Hosts call this at a job checkpoint.</summary>
    public void DrainJobs() => DrainJobs(default);

    public void DrainJobs(CancellationToken cancellationToken)
    {
        ConfigureExecution();
        _interpreter.RunGuarded(() => { _interpreter.DrainJobs(); return JsValue.Undefined; }, cancellationToken);
    }

    /// <summary>Creates a native Promise; settle it only on this engine's owner thread.</summary>
    public JsValue CreatePromise(out Action<JsValue> resolve, out Action<JsValue> reject)
    {
        ConfigureExecution();
        return _interpreter.CreatePromise(out resolve, out reject);
    }

    /// <summary>Queues an owner-thread callback after previously queued Promise jobs.</summary>
    public void EnqueueMicrotask(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ConfigureExecution();
        _interpreter.EnqueueMicrotask(callback);
    }

    /// <summary>Invokes a JavaScript callback subject to engine cancellation and limits.</summary>
    public JsValue Invoke(JsFunction function, JsValue receiver, JsValue[] arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(function);
        ConfigureExecution();
        return _interpreter.RunGuarded(() => function.Call(receiver, arguments), cancellationToken);
    }
    /// <summary>Sets a binding in this engine's global environment.</summary>
    public void SetGlobal(string name, JsValue value) => _interpreter.SetGlobal(name, value);
    /// <summary>Reads a binding from this engine's global environment.</summary>
    public JsValue GetGlobal(string name) => _interpreter.GetGlobal(name);
}
