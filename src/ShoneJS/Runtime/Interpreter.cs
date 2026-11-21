// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Sdcb.Arithmetic;
using ShoneJs.Parsing;

namespace ShoneJs.Runtime;

public sealed partial class Interpreter
{
    private readonly EnvironmentRecord _global = new();
    private readonly JsObject _globalObject = new();
    private readonly JsObject _objectPrototype;
    private readonly JsArray _arrayPrototype;
    private readonly Builtins.PrimitiveBuiltins _primitives;
    private readonly Builtins.PromiseBuiltins _promises;
    private readonly Builtins.RegExpBuiltins _regularExpressions;
    private readonly Dictionary<string, JsObject> _errorPrototypes = new(StringComparer.Ordinal);
    private EnvironmentRecord _env;
    private EnvironmentRecord _varEnv;
    private bool _strict;
    private JsValue _this;
    [ThreadStatic]
    private static Interpreter? _executingInterpreter;
    private int _remainingIterations, _remainingSteps;
    private int _callDepth, _executionDepth;
    private CancellationToken _operationCancellation;
    internal CancellationToken CancellationToken { get; set; }
    internal int MaximumExecutionSteps { get; set; } = 10_000_000;
    internal int MaximumPendingJobs { get => _promises.MaximumPendingJobs; set => _promises.MaximumPendingJobs = value; }
    /// <summary>Maximum loop and queued-job steps permitted by one script execution.</summary>
    public int MaximumLoopIterations { get; set; } = 1_000_000;

    private enum CompletionKind { Normal, Return, Break, Continue }

    private readonly record struct Completion(CompletionKind Kind, JsValue Value)
    {
        public bool IsAbrupt => Kind != CompletionKind.Normal;
        public static Completion Normal(JsValue value) => new(CompletionKind.Normal, value);
        public static Completion Return(JsValue value) => new(CompletionKind.Return, value);
        public static Completion Break => new(CompletionKind.Break, JsValue.Undefined);
        public static Completion Continue => new(CompletionKind.Continue, JsValue.Undefined);
    }

    private static JsValue ScriptCompletionValue(Completion completion) => completion.Kind switch
    {
        CompletionKind.Normal => completion.Value,
        _ => throw UnexpectedCompletion(completion.Kind)
    };

    private static JsValue FunctionCompletionValue(Completion completion) => completion.Kind switch
    {
        CompletionKind.Normal => JsValue.Undefined,
        CompletionKind.Return => completion.Value,
        _ => throw UnexpectedCompletion(completion.Kind)
    };

    // The parser rejects these escapes. The public interpreter also accepts manually built ASTs.
    private static JsException UnexpectedCompletion(CompletionKind kind) =>
        new(JsValue.From(new JsError("SyntaxError", $"Unexpected '{kind.ToString().ToLowerInvariant()}'")));

    public Interpreter() : this(enableBigFloat: false) { }

    internal Interpreter(bool enableBigFloat, bool isolateSymbolRegistry = false)
    {
        _env = _varEnv = _global;
        _objectPrototype = Builtins.ObjectBuiltins.Install(SetGlobal, () => _arrayPrototype);
        _globalObject.Prototype = _objectPrototype;
        _arrayPrototype = Builtins.ArrayBuiltins.Install(SetGlobal, _objectPrototype);
        Builtins.JsonBuiltins.Install(SetGlobal, _objectPrototype, _arrayPrototype);
        _regularExpressions = new Builtins.RegExpBuiltins(SetGlobal, _objectPrototype, _arrayPrototype);
        Builtins.ProxyBuiltins.Install(SetGlobal, _objectPrototype);
        _this = JsValue.From(_globalObject);
        foreach (var (name, value) in new[] { ("undefined", JsValue.Undefined), ("NaN", JsValue.From(double.NaN)), ("Infinity", JsValue.From(double.PositiveInfinity)) })
        {
            _global.Create(name, mutable: false, lexical: false);
            _global.Initialize(name, value);
            _globalObject.DefineOwnProperty(name, PropertyDescriptor.Data(value));
        }
        SetGlobal("globalThis", _this);
        SetGlobal("print", JsValue.From(new JsFunction(args =>
        {
            Console.WriteLine(string.Join(" ", args.Select(x => x.ToJsString())));
            return JsValue.Undefined;
        })));
        _primitives = new Builtins.PrimitiveBuiltins(SetGlobal, _objectPrototype, _arrayPrototype, _regularExpressions);
        SetGlobal("Boolean", JsValue.From(new JsFunction(args => JsValue.From(args.Length > 0 && args[0].ToBoolean()), 1)));
        var bigInt = new JsFunction(args => ToBigInt(args.Length == 0 ? JsValue.Undefined : args[0]), 1);
        bigInt.DefineOwnProperty("name", PropertyDescriptor.Data(JsValue.From("BigInt"), configurable: true));
        foreach (var (name, signed) in new[] { ("asIntN", true), ("asUintN", false) })
        {
            var method = new JsFunction(args => NarrowBigInt(args, signed), 2);
            method.DefineOwnProperty("name", PropertyDescriptor.Data(JsValue.From(name), configurable: true));
            bigInt.DefineOwnProperty(name, PropertyDescriptor.Data(JsValue.From(method), writable: true, configurable: true));
        }
        SetGlobal("BigInt", JsValue.From(bigInt));
        if (enableBigFloat) Builtins.BigFloatBuiltins.Install(SetGlobal, _objectPrototype);
        InstallSymbol(isolateSymbolRegistry ? new(StringComparer.Ordinal) : SymbolRegistry);
        Builtins.WeakBuiltins.Install(SetGlobal, _objectPrototype, static symbol => symbol.RegistryKey is not null);
        Builtins.TypedArrayBuiltins.Install(SetGlobal, _objectPrototype);
        foreach (var name in new[] { "Error", "TypeError", "ReferenceError", "RangeError", "SyntaxError", "EvalError", "URIError" })
        {
            var prototype = new JsObject { Prototype = _errorPrototypes.GetValueOrDefault("Error") ?? _objectPrototype };
            _errorPrototypes.Add(name, prototype);
            var constructor = new JsFunction(args => JsValue.From(new JsError(name, args.Length == 0 || args[0].Type == JsType.Undefined ? "" : args[0].ToJsString()) { Prototype = prototype }), 1) { DefaultConstructionPrototype = _objectPrototype };
            constructor.Set("prototype", JsValue.From(prototype));
            prototype.Set("constructor", JsValue.From(constructor));
            SetGlobal(name, JsValue.From(constructor));
        }
        _promises = new Builtins.PromiseBuiltins(_objectPrototype, _arrayPrototype, AttachErrorPrototype, ConsumeStep);
        _promises.Install(SetGlobal);
    }

    private static readonly ConcurrentDictionary<string, JsSymbol> SymbolRegistry = new(StringComparer.Ordinal);

    private void InstallSymbol(ConcurrentDictionary<string, JsSymbol> registry)
    {
        var symbol = new JsFunction(args => JsValue.From(new JsSymbol(args.Length == 0 || args[0].Type == JsType.Undefined
            ? null : AbstractOperations.ToPrimitive(args[0], preferString: true).ToJsString())));
        AddMethod("for", args =>
        {
            var key = AbstractOperations.ToPrimitive(args.Length > 0 ? args[0] : JsValue.Undefined, preferString: true).ToJsString();
            return JsValue.From(registry.GetOrAdd(key, static name => new JsSymbol(name) { RegistryKey = name }));
        });
        AddMethod("keyFor", args =>
        {
            if (args.Length == 0 || args[0].Data is not JsSymbol input)
                throw JsException.TypeError("Symbol.keyFor requires a symbol");
            return input.RegistryKey is { } key ? JsValue.From(key) : JsValue.Undefined;
        });
        SetGlobal("Symbol", JsValue.From(symbol));

        void AddMethod(string name, Func<JsValue[], JsValue> call)
        {
            var method = new JsFunction(call, 1);
            method.DefineOwnProperty("name", PropertyDescriptor.Data(JsValue.From(name), configurable: true));
            symbol.DefineOwnProperty(name, PropertyDescriptor.Data(JsValue.From(method), writable: true, configurable: true));
        }
    }

    /// <summary>Runs pending promise jobs synchronously, subject to the execution iteration limit.</summary>
    public void DrainJobs() => DrainJobs(default);

    internal void DrainJobs(CancellationToken cancellationToken) =>
        RunGuarded(() => { _promises.DrainJobs(ConsumeIteration); return JsValue.Undefined; }, cancellationToken);

    internal JsValue RunGuarded(Func<JsValue> run, CancellationToken cancellationToken = default)
    {
        var previous = _operationCancellation;
        var previousInterpreter = _executingInterpreter;
        CancellationTokenSource? linked = null;
        if (_executionDepth == 0)
        {
            _remainingSteps = MaximumExecutionSteps;
            _remainingIterations = MaximumLoopIterations;
        }
        if (cancellationToken.CanBeCanceled && previous.CanBeCanceled && cancellationToken != previous)
            linked = CancellationTokenSource.CreateLinkedTokenSource(previous, cancellationToken);
        _operationCancellation = linked?.Token ?? (cancellationToken.CanBeCanceled ? cancellationToken : previous);
        _executionDepth++;
        _executingInterpreter = this;
        try
        {
            CheckCancellation();
            return run();
        }
        finally
        {
            _executionDepth--;
            _executingInterpreter = previousInterpreter;
            _operationCancellation = previous;
            linked?.Dispose();
        }
    }

    private void CheckCancellation()
    {
        CancellationToken.ThrowIfCancellationRequested();
        _operationCancellation.ThrowIfCancellationRequested();
    }

    private void ConsumeStep()
    {
        CheckCancellation();
        if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
            throw new InvalidOperationException("Execution nesting limit exceeded");
        if (_executionDepth > 0 && _remainingSteps-- <= 0) throw new InvalidOperationException("Execution step limit exceeded");
    }

    /// <summary>Charges work performed by a native callback to the active script execution budget.</summary>
    public static void ChargeNativeWork(long work = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(work);
        if (_executingInterpreter is not { } engine) return;
        engine.CheckCancellation();
        if (work > engine._remainingSteps) throw new InvalidOperationException("Execution step limit exceeded");
        engine._remainingSteps -= (int)work;
    }

    internal JsValue CreatePromise(out Action<JsValue> resolve, out Action<JsValue> reject)
    {
        CheckCancellation();
        var promise = _promises.Create(out var fulfill, out var fail);
        resolve = value => RunGuarded(() => { fulfill(value); return JsValue.Undefined; });
        reject = value => RunGuarded(() => { fail(value); return JsValue.Undefined; });
        return promise;
    }

    internal void EnqueueMicrotask(Action callback)
    {
        CheckCancellation();
        _promises.Enqueue(callback);
    }

    private static JsValue NarrowBigInt(JsValue[] args, bool signed)
    {
        var bitCount = AbstractOperations.ToPrimitive(args.Length > 0 ? args[0] : JsValue.Undefined).ToNumber();
        bitCount = double.IsNaN(bitCount) ? 0 : Math.Truncate(bitCount);
        if (bitCount < 0 || bitCount > 9_007_199_254_740_991d)
            throw JsException.RangeError("Invalid BigInt bit count");

        var converted = ToBigInt(args.Length > 1 ? args[1] : JsValue.Undefined, allowNumber: false);
        var integer = (BigInt)converted.Data!;
        if (bitCount == 0 || integer.IsZero) return JsValue.From(BigInt.Zero);
        if (integer.GetBitLength() < bitCount && (signed || integer.Sign >= 0)) return converted;
        if (bitCount > MaximumBigIntResultBits) throw JsException.RangeError("BigInt result is too large");

        var modulus = BigInt.One << (int)bitCount;
        var remainder = integer & (modulus - BigInt.One);
        if (signed && remainder >= (modulus >> 1)) remainder -= modulus;
        return JsValue.From(remainder);
    }

    private static JsValue ToBigInt(JsValue value, bool allowNumber = true)
    {
        value = AbstractOperations.ToPrimitive(value);
        if (value.Type == JsType.BigInt) return value;
        if (value.Type == JsType.Boolean) return JsValue.From(new BigInt(value.ToNumber()));
        if (allowNumber && value.Type == JsType.Number)
        {
            var number = value.ToNumber();
            if (!double.IsFinite(number) || number != Math.Truncate(number)) throw JsException.RangeError("Number is not an integer");
            return JsValue.From(new BigInt(number));
        }
        if (value.Type == JsType.String)
        {
            if (AbstractOperations.TryParseBigInt((string)value.Data!, out var integer)) return JsValue.From(integer);
            throw new JsException(JsValue.From(new JsError("SyntaxError", "Invalid BigInt string")));
        }
        throw JsException.TypeError("Cannot convert value to BigInt");
    }

    /// <summary>Sets a global binding and mirrors non-lexical bindings on the global object.</summary>
    public void SetGlobal(string name, JsValue value)
    {
        if (_global.HasOwn(name)) _global.Set(name, value);
        else _global.Declare(name, value);
        if (!_global.HasLexical(name)) _globalObject.Set(name, value);
    }

    /// <summary>Reads a global binding, preferring lexical declarations over global-object properties.</summary>
    public JsValue GetGlobal(string name)
    {
        if (_global.HasLexical(name)) return _global.Get(name);
        if (_globalObject.HasProperty(name)) return _globalObject.Get(name);
        throw JsException.ReferenceError($"'{name}' is not defined");
    }

    /// <summary>Executes parsed statements as a non-strict script and returns its completion value.</summary>
    public JsValue Execute(List<Stmt> statements) => Execute(statements, false);

    internal JsValue Execute(List<Stmt> statements, bool strict, CancellationToken cancellationToken = default) =>
        RunGuarded(() => ExecuteScript(statements, strict), cancellationToken);

    private JsValue ExecuteScript(List<Stmt> statements, bool strict)
    {
        var previous = CaptureContext();
        _env = _varEnv = _global;
        _strict = strict;
        _this = JsValue.From(_globalObject);
        _activeModule = null;
        try
        {
            PrepareScope(statements, _env, functionScope: true);
            return ScriptCompletionValue(ExecuteList(statements));
        }
        catch (JsException error)
        {
            AttachErrorPrototype(error);
            throw;
        }
        finally
        {
            RestoreContext(previous);
        }
    }

    private Completion ExecuteList(IEnumerable<Stmt> statements)
    {
        var result = Completion.Normal(JsValue.Undefined);
        foreach (var statement in statements)
        {
            var completion = Exec(statement);
            if (completion.IsAbrupt) return completion;
            if (ProducesListValue(statement)) result = completion;
        }
        return result;
    }

    private static bool ProducesListValue(Stmt statement) =>
        statement is not (VarStmt or VarListStmt or FunctionStmt or TypeAliasStmt or InterfaceStmt or EnumStmt);

    private static IEnumerable<VarStmt> ImmediateVariables(IEnumerable<Stmt> statements) => statements.SelectMany(statement => statement switch
    {
        VarStmt variable => [variable],
        VarListStmt list => list.Declarations,
        _ => Enumerable.Empty<VarStmt>()
    });

    private IEnumerable<VarStmt> HoistedVariables(IEnumerable<Stmt> statements)
    {
        foreach (var statement in statements)
        {
            ConsumeStep();
            var variables = statement switch
            {
                VarStmt { Kind: TokenKind.Var } variable => [variable],
                VarListStmt list => list.Declarations.Where(variable => variable.Kind == TokenKind.Var),
                BlockStmt block => HoistedVariables(block.Body),
                WhileStmt loop => HoistedVariables([loop.Body]),
                DoWhileStmt loop => HoistedVariables([loop.Body]),
                ForStmt loop => HoistedVariables(loop.Init is null ? [loop.Body] : [loop.Init, loop.Body]),
                IfStmt branch => HoistedVariables(branch.Else is null ? [branch.Then] : [branch.Then, branch.Else]),
                TryStmt attempt => HoistedVariables(new[] { attempt.Body, attempt.CatchBody, attempt.FinallyBody }.OfType<Stmt>()),
                SwitchStmt selection => HoistedVariables(selection.Cases.SelectMany(clause => clause.Body)),
                _ => Enumerable.Empty<VarStmt>()
            };
            foreach (var variable in variables) yield return variable;
        }
    }

    private void PrepareScope(List<Stmt> statements, EnvironmentRecord scope, bool functionScope = false, EnvironmentRecord? parameterScope = null)
    {
        var lexical = ImmediateVariables(statements).Where(variable => variable.Kind != TokenKind.Var).ToArray();
        var functionDeclarations = statements.OfType<FunctionStmt>().ToArray();
        var enums = statements.OfType<EnumStmt>().ToArray();
        var varNames = HoistedVariables(statements).Select(variable => variable.Name).ToHashSet(StringComparer.Ordinal);
        var lexicalNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variable in lexical)
        {
            if (!lexicalNames.Add(variable.Name) || scope.HasOwn(variable.Name) || varNames.Contains(variable.Name) || parameterScope?.HasOwn(variable.Name) == true)
                throw new JsSyntaxException($"Identifier '{variable.Name}' has already been declared", variable.Span.Start.Line, variable.Span.Start.Column);
        }
        foreach (var declaration in enums)
        {
            if (!lexicalNames.Add(declaration.Name) || scope.HasOwn(declaration.Name) || varNames.Contains(declaration.Name) || parameterScope?.HasOwn(declaration.Name) == true)
                throw new JsSyntaxException($"Identifier '{declaration.Name}' has already been declared", declaration.Span.Start.Line, declaration.Span.Start.Column);
        }
        foreach (var function in functionDeclarations)
        {
            if (lexicalNames.Contains(function.Name) || scope.HasLexical(function.Name) || !functionScope && !lexicalNames.Add(function.Name))
                throw new JsSyntaxException($"Identifier '{function.Name}' has already been declared", function.Span.Start.Line, function.Span.Start.Column);
        }
        if (functionScope)
        {
            foreach (var name in varNames)
                if (_varEnv.HasLexical(name)) throw new JsSyntaxException($"Identifier '{name}' has already been declared", 1, 1);
        }

        foreach (var variable in lexical) scope.Create(variable.Name, variable.Kind == TokenKind.Let, lexical: true);
        foreach (var declaration in enums) scope.Create(declaration.Name, mutable: true, lexical: true);
        if (functionScope)
            foreach (var name in varNames)
                if (!_varEnv.HasOwn(name)) DeclareVar(_varEnv, name, JsValue.Undefined);

        foreach (var function in functionDeclarations)
        {
            var value = MakeFunction(function.Parameters, function.Body, scope, isAsync: function.IsAsync);
            var target = functionScope ? _varEnv : scope;
            if (target.HasOwn(function.Name)) WriteBinding(target, function.Name, value);
            else if (functionScope) DeclareVar(target, function.Name, value);
            else
            {
                target.Create(function.Name, mutable: true, lexical: true);
                target.Initialize(function.Name, value);
            }
        }
    }

    private void DeclareVar(EnvironmentRecord target, string name, JsValue value)
    {
        target.Declare(name, value);
        if (target == _global && !_globalObject.HasOwn(name))
            _globalObject.DefineOwnProperty(name, PropertyDescriptor.Data(value, writable: true, enumerable: true));
    }

    private void WriteBinding(EnvironmentRecord target, string name, JsValue value)
    {
        if (target == _global && !_global.HasLexical(name))
        {
            if (!_globalObject.Set(name, value, JsValue.From(_globalObject)) && _strict)
                throw JsException.TypeError($"Cannot assign to global '{name}'");
            return;
        }
        target.Set(name, value);
    }

    private Completion Exec(Stmt statement)
    {
        ConsumeStep();
        return ExecCore(statement);
    }

    private Completion ExecCore(Stmt statement) => statement switch
    {
        BlockStmt block => ExecuteBlock(block),
        VarStmt variable => Declare(variable),
        VarListStmt list => ExecuteList(list.Declarations),
        FunctionStmt or TypeAliasStmt or InterfaceStmt => Completion.Normal(JsValue.Undefined),
        EnumStmt declaration => Completion.Normal(ExecuteEnum(declaration)),
        ExprStmt expression => Completion.Normal(Eval(expression.Expression)),
        IfStmt branch => Eval(branch.Test).ToBoolean() ? Exec(branch.Then)
            : branch.Else is null ? Completion.Normal(JsValue.Undefined) : Exec(branch.Else),
        WhileStmt loop => While(loop.Test, loop.Body),
        DoWhileStmt loop => While(loop.Test, loop.Body, runFirst: true),
        ForStmt loop => For(loop),
        SwitchStmt selection => Switch(selection),
        TryStmt attempt => Try(attempt),
        BreakStmt => Completion.Break,
        ContinueStmt => Completion.Continue,
        ReturnStmt returned => Completion.Return(returned.Value is null ? JsValue.Undefined : Eval(returned.Value)),
        ThrowStmt thrown => throw new JsException(Eval(thrown.Value)),
        _ => throw new NotSupportedException($"Unsupported statement {statement.GetType().Name}")
    };

    private Completion ExecuteBlock(BlockStmt block)
    {
        var previous = _env;
        _env = new EnvironmentRecord(previous);
        try
        {
            PrepareScope(block.Body, _env);
            return ExecuteList(block.Body);
        }
        finally { _env = previous; }
    }

    private Completion Declare(VarStmt variable) =>
        InitializeVariable(variable, variable.Init is null ? JsValue.Undefined : Eval(variable.Init));

    private Completion InitializeVariable(VarStmt variable, JsValue value)
    {
        if (variable.Kind != TokenKind.Var) _env.Initialize(variable.Name, value);
        else if (variable.Init is not null) WriteBinding(_varEnv, variable.Name, value);
        return Completion.Normal(JsValue.Undefined);
    }

    private void ConsumeIteration()
    {
        ConsumeStep();
        if (_remainingIterations <= 0) throw new InvalidOperationException("Execution limit exceeded");
        _remainingIterations--;
    }

    private Completion While(Expr test, Stmt body, bool runFirst = false)
    {
        var result = Completion.Normal(JsValue.Undefined);
        while (runFirst || Eval(test).ToBoolean())
        {
            runFirst = false;
            ConsumeIteration();
            var completion = Exec(body);
            if (completion.Kind == CompletionKind.Break) break;
            if (completion.Kind == CompletionKind.Return) return completion;
            if (!completion.IsAbrupt) result = completion;
        }
        return result;
    }

    private Completion For(ForStmt loop)
    {
        var previous = _env;
        try
        {
            var lexical = PrepareForScope(loop, previous);
            if (loop.Init is not null)
            {
                var initial = Exec(loop.Init);
                if (initial.IsAbrupt) return initial;
            }
            var result = Completion.Normal(JsValue.Undefined);
            while (loop.Test is null || Eval(loop.Test).ToBoolean())
            {
                ConsumeIteration();
                var completion = Exec(loop.Body);
                if (completion.Kind == CompletionKind.Break) break;
                if (completion.Kind == CompletionKind.Return) return completion;
                if (!completion.IsAbrupt) result = completion;
                AdvanceForScope(lexical, previous);
                if (loop.Update is not null) Eval(loop.Update);
            }
            return result;
        }
        finally { _env = previous; }
    }

    private VarStmt[] PrepareForScope(ForStmt loop, EnvironmentRecord parent)
    {
        var lexical = loop.Init is null ? [] : ImmediateVariables([loop.Init]).Where(variable => variable.Kind != TokenKind.Var).ToArray();
        if (lexical.Length > 0)
        {
            _env = new EnvironmentRecord(parent);
            foreach (var variable in lexical) _env.Create(variable.Name, variable.Kind == TokenKind.Let, lexical: true);
        }
        return lexical;
    }

    private void AdvanceForScope(VarStmt[] lexical, EnvironmentRecord parent)
    {
        if (lexical.Length == 0) return;
        var current = _env;
        _env = new EnvironmentRecord(parent);
        foreach (var variable in lexical)
        {
            _env.Create(variable.Name, variable.Kind == TokenKind.Let, lexical: true);
            _env.Initialize(variable.Name, current.Get(variable.Name));
        }
    }

    private Completion Switch(SwitchStmt selection)
    {
        var value = Eval(selection.Discriminant);
        var previous = _env;
        _env = new EnvironmentRecord(previous);
        try
        {
            PrepareScope(selection.Cases.SelectMany(clause => clause.Body).ToList(), _env);
            var match = -1;
            var fallback = -1;
            for (var i = 0; i < selection.Cases.Count; i++)
            {
                var test = selection.Cases[i].Test;
                if (test is null) fallback = i;
                else if (AbstractOperations.StrictEqual(Eval(test), value)) { match = i; break; }
            }
            if (match < 0) match = fallback;
            var result = Completion.Normal(JsValue.Undefined);
            if (match < 0) return result;
            for (var i = match; i < selection.Cases.Count; i++)
            {
                var completion = ExecuteList(selection.Cases[i].Body);
                if (completion.Kind == CompletionKind.Break) break;
                if (completion.IsAbrupt) return completion;
                result = completion;
            }
            return result;
        }
        finally { _env = previous; }
    }

    private Completion Try(TryStmt attempt)
    {
        var result = default(Completion);
        ExceptionDispatchInfo? pendingThrow = null;
        try
        {
            try { result = ExecuteBlock(attempt.Body); }
            catch (JsException error) when (attempt.CatchBody is not null)
            {
                var previous = _env;
                try
                {
                    EnterCatchScope(attempt, error);
                    result = ExecuteBlock(attempt.CatchBody);
                }
                finally { _env = previous; }
            }
        }
        catch (JsException error) when (attempt.FinallyBody is not null)
        {
            pendingThrow = ExceptionDispatchInfo.Capture(error);
        }

        // Host failures escape before JS finally runs, including cancellation and budget failures.
        return attempt.FinallyBody is null ? result
            : FinishTry(result, pendingThrow, ExecuteBlock(attempt.FinallyBody));
    }

    private void EnterCatchScope(TryStmt attempt, JsException error)
    {
        AttachErrorPrototype(error);
        _env = new EnvironmentRecord(_env);
        if (attempt.CatchName is not null) _env.Declare(attempt.CatchName, error.Value);
    }

    private static Completion FinishTry(Completion result, ExceptionDispatchInfo? pendingThrow, Completion final)
    {
        if (final.IsAbrupt) return final;
        pendingThrow?.Throw();
        return result;
    }

    private void AttachErrorPrototype(JsException error)
    {
        if (error.Value.Data is JsError { Prototype: null } value && _errorPrototypes.TryGetValue(value.Name, out var prototype))
            value.Prototype = prototype;
    }

    private JsValue MakeFunction(List<string> parameters, BlockStmt? body, EnvironmentRecord closure, string? name = null,
        bool isMethod = false, JsValue? lexicalThis = null, Expr? expressionBody = null, bool isAsync = false)
    {
        if (name is not null) closure = new EnvironmentRecord(closure);
        var strict = _strict || body?.IsStrict == true;
        var constructible = !isAsync && !isMethod && lexicalThis is null;
        var module = _activeModule;
        var function = new JsFunction((receiver, arguments) => RunGuarded(() =>
        {
            ConsumeStep();
            if (_callDepth >= 512) throw new InvalidOperationException("Call depth limit exceeded");
            _callDepth++;
            var previous = CaptureContext();
            _varEnv = new EnvironmentRecord(closure);
            _env = new EnvironmentRecord(_varEnv);
            _strict = strict;
            _activeModule = module;
            _this = lexicalThis ?? (!strict && AbstractOperations.IsNullish(receiver) ? JsValue.From(_globalObject) : receiver);
            try
            {
                if (isAsync) return StartAsync(() => InvokeAsyncFunction(parameters, arguments, body, expressionBody));
                BindParameters(parameters, arguments);
                if (expressionBody is not null) return Eval(expressionBody);
                PrepareScope(body!.Body, _env, functionScope: true, parameterScope: _varEnv);
                return FunctionCompletionValue(ExecuteList(body.Body));
            }
            finally
            {
                RestoreContext(previous);
                _callDepth--;
            }
        }), parameters.Count) { DefaultConstructionPrototype = constructible ? _objectPrototype : null };
        function.Prototype = _objectPrototype;
        if (constructible)
        {
            var prototype = new JsObject { Prototype = _objectPrototype };
            prototype.DefineOwnProperty("constructor", PropertyDescriptor.Data(JsValue.From(function), writable: true, configurable: true));
            function.DefineOwnProperty("prototype", PropertyDescriptor.Data(JsValue.From(prototype), writable: true));
        }
        var functionValue = JsValue.From(function);
        if (name is not null)
        {
            closure.Create(name, mutable: false, lexical: true);
            closure.Initialize(name, functionValue);
        }
        return functionValue;
    }

    private void BindParameters(List<string> parameters, JsValue[] arguments)
    {
        for (var i = 0; i < parameters.Count; i++)
        {
            ConsumeStep();
            var value = i < arguments.Length ? arguments[i] : JsValue.Undefined;
            if (_varEnv.HasOwn(parameters[i])) _varEnv.Set(parameters[i], value);
            else _varEnv.Declare(parameters[i], value);
        }
    }
}
