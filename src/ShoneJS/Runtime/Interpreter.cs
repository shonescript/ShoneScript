// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using Sdcb.Arithmetic;
using ShoneJs.Parsing;

namespace ShoneJs.Runtime;

public sealed partial class Interpreter
{
    private readonly EnvironmentRecord _global = new();
    private readonly JsObject _globalObject = new();
    private readonly JsObject _objectPrototype;
    private readonly JsArray _arrayPrototype;
    private readonly Builtins.PromiseBuiltins _promises;
    private readonly Dictionary<string, JsObject> _errorPrototypes = new(StringComparer.Ordinal);
    private EnvironmentRecord _env;
    private EnvironmentRecord _varEnv;
    private bool _strict;
    private JsValue _this;
    private int _remainingIterations;
    private int _callDepth;
    private int _scriptDepth;
    /// <summary>Maximum loop and queued-job steps permitted by one script execution.</summary>
    public int MaximumLoopIterations { get; set; } = 1_000_000;

    private sealed class ReturnSignal(JsValue value) : Exception { public JsValue Value { get; } = value; }
    private sealed class BreakSignal : Exception;
    private sealed class ContinueSignal : Exception;

    public Interpreter()
    {
        _env = _varEnv = _global;
        _objectPrototype = Builtins.ObjectBuiltins.Install(SetGlobal, () => _arrayPrototype);
        _globalObject.Prototype = _objectPrototype;
        _arrayPrototype = Builtins.ArrayBuiltins.Install(SetGlobal, _objectPrototype);
        Builtins.JsonBuiltins.Install(SetGlobal, _objectPrototype, _arrayPrototype);
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
        SetGlobal("String", JsValue.From(new JsFunction(args =>
        {
            if (args.Length == 0) return JsValue.From("");
            var input = args[0];
            return JsValue.From(input.Type == JsType.Symbol ? input.ToString()
                : AbstractOperations.ToPrimitive(input, preferString: true).ToJsString());
        }, 1)));
        SetGlobal("Number", JsValue.From(new JsFunction(args =>
        {
            if (args.Length == 0) return JsValue.From(0d);
            var input = AbstractOperations.ToPrimitive(args[0]);
            return JsValue.From(input.Data is BigInt integer ? (double)integer : input.ToNumber());
        }, 1)));
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
        InstallSymbol();
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
        _promises = new Builtins.PromiseBuiltins(_objectPrototype, _arrayPrototype, AttachErrorPrototype);
        _promises.Install(SetGlobal);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, JsSymbol> SymbolRegistry = new(StringComparer.Ordinal);

    private void InstallSymbol()
    {
        var symbol = new JsFunction(args => JsValue.From(new JsSymbol(args.Length == 0 || args[0].Type == JsType.Undefined
            ? null : AbstractOperations.ToPrimitive(args[0], preferString: true).ToJsString())));
        AddMethod("for", args =>
        {
            var key = AbstractOperations.ToPrimitive(args.Length > 0 ? args[0] : JsValue.Undefined, preferString: true).ToJsString();
            return JsValue.From(SymbolRegistry.GetOrAdd(key, static name => new JsSymbol(name) { RegistryKey = name }));
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
    public void DrainJobs()
    {
        var remaining = _remainingIterations;
        if (_scriptDepth == 0) _remainingIterations = MaximumLoopIterations;
        _scriptDepth++;
        try { _promises.DrainJobs(ConsumeIteration); }
        finally { _scriptDepth--; _remainingIterations = remaining; }
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

    internal JsValue Execute(List<Stmt> statements, bool strict)
    {
        var previous = (_env, _varEnv, _strict, _this, _remainingIterations);
        _env = _varEnv = _global;
        _strict = strict;
        _this = JsValue.From(_globalObject);
        _remainingIterations = MaximumLoopIterations;
        _scriptDepth++;
        try
        {
            PrepareScope(statements, _env, functionScope: true);
            return ExecuteList(statements);
        }
        catch (JsException error)
        {
            AttachErrorPrototype(error);
            throw;
        }
        finally
        {
            (_env, _varEnv, _strict, _this, _remainingIterations) = previous;
            _scriptDepth--;
        }
    }

    private JsValue ExecuteList(IEnumerable<Stmt> statements)
    {
        var result = JsValue.Undefined;
        foreach (var statement in statements)
        {
            var value = Exec(statement);
            if (statement is not (VarStmt or VarListStmt or FunctionStmt or TypeAliasStmt or InterfaceStmt or EnumStmt)) result = value;
        }
        return result;
    }

    private static IEnumerable<VarStmt> ImmediateVariables(IEnumerable<Stmt> statements) => statements.SelectMany(statement => statement switch
    {
        VarStmt variable => [variable],
        VarListStmt list => list.Declarations,
        _ => Enumerable.Empty<VarStmt>()
    });

    private static IEnumerable<VarStmt> HoistedVariables(IEnumerable<Stmt> statements)
    {
        foreach (var statement in statements)
        {
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
            var value = MakeFunction(function.Parameters, function.Body, scope);
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

    private JsValue Exec(Stmt statement) => statement switch
    {
        BlockStmt block => ExecuteBlock(block),
        VarStmt variable => Declare(variable),
        VarListStmt list => ExecuteList(list.Declarations),
        FunctionStmt or TypeAliasStmt or InterfaceStmt => JsValue.Undefined,
        EnumStmt declaration => ExecuteEnum(declaration),
        ExprStmt expression => Eval(expression.Expression),
        IfStmt branch => Eval(branch.Test).ToBoolean() ? Exec(branch.Then) : branch.Else is null ? JsValue.Undefined : Exec(branch.Else),
        WhileStmt loop => While(loop.Test, loop.Body),
        DoWhileStmt loop => While(loop.Test, loop.Body, runFirst: true),
        ForStmt loop => For(loop),
        SwitchStmt selection => Switch(selection),
        TryStmt attempt => Try(attempt),
        BreakStmt => throw new BreakSignal(),
        ContinueStmt => throw new ContinueSignal(),
        ReturnStmt returned => throw new ReturnSignal(returned.Value is null ? JsValue.Undefined : Eval(returned.Value)),
        ThrowStmt thrown => throw new JsException(Eval(thrown.Value)),
        _ => throw new NotSupportedException($"Unsupported statement {statement.GetType().Name}")
    };

    private JsValue ExecuteBlock(BlockStmt block)
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

    private JsValue Declare(VarStmt variable)
    {
        if (variable.Kind == TokenKind.Var)
        {
            if (variable.Init is not null) WriteBinding(_varEnv, variable.Name, Eval(variable.Init));
        }
        else _env.Initialize(variable.Name, variable.Init is null ? JsValue.Undefined : Eval(variable.Init));
        return JsValue.Undefined;
    }

    private void ConsumeIteration()
    {
        if (_remainingIterations <= 0) throw new InvalidOperationException("Execution limit exceeded");
        _remainingIterations--;
    }

    private JsValue While(Expr test, Stmt body, bool runFirst = false)
    {
        var result = JsValue.Undefined;
        while (runFirst || Eval(test).ToBoolean())
        {
            runFirst = false;
            ConsumeIteration();
            try { result = Exec(body); }
            catch (ContinueSignal) { }
            catch (BreakSignal) { break; }
        }
        return result;
    }

    private JsValue For(ForStmt loop)
    {
        var previous = _env;
        try
        {
            var lexical = loop.Init is null ? [] : ImmediateVariables([loop.Init]).Where(variable => variable.Kind != TokenKind.Var).ToArray();
            if (lexical.Length > 0)
            {
                _env = new EnvironmentRecord(previous);
                foreach (var variable in lexical) _env.Create(variable.Name, variable.Kind == TokenKind.Let, lexical: true);
            }
            if (loop.Init is not null) Exec(loop.Init);
            var result = JsValue.Undefined;
            while (loop.Test is null || Eval(loop.Test).ToBoolean())
            {
                ConsumeIteration();
                try { result = Exec(loop.Body); }
                catch (ContinueSignal) { }
                catch (BreakSignal) { break; }
                if (lexical.Length > 0)
                {
                    var current = _env;
                    _env = new EnvironmentRecord(previous);
                    foreach (var variable in lexical)
                    {
                        _env.Create(variable.Name, variable.Kind == TokenKind.Let, lexical: true);
                        _env.Initialize(variable.Name, current.Get(variable.Name));
                    }
                }
                if (loop.Update is not null) Eval(loop.Update);
            }
            return result;
        }
        finally { _env = previous; }
    }

    private JsValue Switch(SwitchStmt selection)
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
            var result = JsValue.Undefined;
            if (match < 0) return result;
            try
            {
                for (var i = match; i < selection.Cases.Count; i++) result = ExecuteList(selection.Cases[i].Body);
            }
            catch (BreakSignal) { }
            return result;
        }
        finally { _env = previous; }
    }

    private JsValue Try(TryStmt attempt)
    {
        try { return ExecuteBlock(attempt.Body); }
        catch (JsException error) when (attempt.CatchBody is not null)
        {
            AttachErrorPrototype(error);
            var previous = _env;
            _env = new EnvironmentRecord(previous);
            try
            {
                if (attempt.CatchName is not null) _env.Declare(attempt.CatchName, error.Value);
                return ExecuteBlock(attempt.CatchBody);
            }
            finally { _env = previous; }
        }
        finally
        {
            if (attempt.FinallyBody is not null) ExecuteBlock(attempt.FinallyBody);
        }
    }

    private void AttachErrorPrototype(JsException error)
    {
        if (error.Value.Data is JsError { Prototype: null } value && _errorPrototypes.TryGetValue(value.Name, out var prototype))
            value.Prototype = prototype;
    }

    private JsValue MakeFunction(List<string> parameters, BlockStmt? body, EnvironmentRecord closure, string? name = null,
        bool isMethod = false, JsValue? lexicalThis = null, Expr? expressionBody = null)
    {
        if (name is not null) closure = new EnvironmentRecord(closure);
        var strict = _strict || body?.IsStrict == true;
        var constructible = !isMethod && lexicalThis is null;
        var function = new JsFunction((receiver, arguments) =>
        {
            if (_callDepth >= 512) throw new InvalidOperationException("Call depth limit exceeded");
            if (_callDepth == 0 && _scriptDepth == 0) _remainingIterations = MaximumLoopIterations;
            _callDepth++;
            var previous = (_env, _varEnv, _strict, _this);
            _varEnv = new EnvironmentRecord(closure);
            _env = new EnvironmentRecord(_varEnv);
            _strict = strict;
            _this = lexicalThis ?? (!strict && AbstractOperations.IsNullish(receiver) ? JsValue.From(_globalObject) : receiver);
            try
            {
                for (var i = 0; i < parameters.Count; i++)
                {
                    var value = i < arguments.Length ? arguments[i] : JsValue.Undefined;
                    if (_varEnv.HasOwn(parameters[i])) _varEnv.Set(parameters[i], value);
                    else _varEnv.Declare(parameters[i], value);
                }
                if (expressionBody is not null) return Eval(expressionBody);
                PrepareScope(body!.Body, _env, functionScope: true, parameterScope: _varEnv);
                ExecuteList(body.Body);
                return JsValue.Undefined;
            }
            catch (ReturnSignal returned) { return returned.Value; }
            finally
            {
                (_env, _varEnv, _strict, _this) = previous;
                _callDepth--;
            }
        }, parameters.Count) { DefaultConstructionPrototype = constructible ? _objectPrototype : null };
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
}
