// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Runtime.ExceptionServices;
using Sdcb.Arithmetic;
using ShonePy.Parsing;
using ShonePy.Modules;

namespace ShonePy.Runtime;

/// <summary>Executes the supported Python syntax tree.</summary>
public sealed partial class PyInterpreter
{
    private readonly PythonOptions _options;
    private readonly PyScope _builtins = new();
    private readonly PyScope _globals;
    private readonly PythonModuleLoader _moduleLoader;
    private readonly Dictionary<string, PyDict> _managedModules = new(StringComparer.Ordinal);
    private PyDict? _currentModule;
    private PyScope _scope;
    private PythonException? _activeException;
    private int _steps;
    private int _callDepth;
    private bool _executionActive;

    public PyInterpreter(PythonOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _globals = new PyScope(null, _builtins);
        _scope = _globals;
        _moduleLoader = new PythonModuleLoader(options.SearchPaths, ExecuteImportedModule);
        InitializeBuiltins();
    }

    public PyValue Execute(ModuleSyntax module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return RunWithBudget(() => ExecuteModule(module), module.FileName);
    }

    private PyValue RunWithBudget(Func<PyValue> execute, string? fileName = null)
    {
        if (_executionActive) return execute();

        _steps = 0;
        _executionActive = true;
        var previousCharge = PyIterator.ChargeStep;
        PyIterator.ChargeStep = depth =>
        {
            if (depth > _options.MaximumCallDepth)
                throw new BudgetSignal(Error("RecursionError", "maximum iterator nesting depth exceeded"));
            Tick();
        };
        try { return execute(); }
        catch (BudgetSignal limit) { throw AddFileName(limit.Error, fileName); }
        catch (PythonException error) when (error.FileName is null && fileName is not null)
        {
            throw AddFileName(error, fileName);
        }
        finally
        {
            PyIterator.ChargeStep = previousCharge;
            _executionActive = false;
        }
    }

    private PyValue ExecuteModule(ModuleSyntax module)
    {
        try { return ExecuteBlock(module.Statements, returnLastExpression: true); }
        catch (ReturnSignal) { throw Error("SyntaxError", "'return' outside function"); }
        catch (BreakSignal) { throw Error("SyntaxError", "'break' outside loop"); }
        catch (ContinueSignal) { throw Error("SyntaxError", "'continue' not properly in loop"); }
    }

    public void SetGlobal(string name, PyValue value) => _globals.Define(name, value);
    public PyValue GetGlobal(string name) => _globals.Get(name);

    private void InitializeBuiltins()
    {
        PyExceptions.RegisterBuiltins(_builtins);
        _builtins.Define("object", PyValue.From(PyClass.Object));
        _builtins.Define("super", PyValue.From(new PyCallable(CreateSuper, "super")));
        _builtins.Define("aiter", PyValue.From(new PyCallable(AsyncIteratorBuiltin, "aiter")));
        _builtins.Define("anext", PyValue.From(new PyCallable(AsyncNextBuiltin, "anext")));
        _builtins.Define("print", PyValue.From(new PyCallable(args =>
        {
            _options.Output.WriteLine(string.Join(" ", args.Select(x => x.ToPythonString())));
            return PyValue.None;
        }, "print")));
        foreach (string name in new[] { "len", "range", "bytes", "repr", "str", "int", "float", "bool", "list", "tuple", "dict", "set", "iter", "next", "abs", "min", "max", "sum", "enumerate", "zip", "sorted", "reversed", "any", "all", "round", "hash", "isinstance", "type", "staticmethod", "classmethod", "property" })
        {
            try { _builtins.Define(name, PyBuiltins.Get(name)); }
            catch (PythonException) { }
        }
    }

    private PyValue CreateSuper(IReadOnlyList<PyValue> args)
    {
        if (args.Count == 2)
        {
            if (args[0].Data is not PyClass cls)
                throw Error("TypeError", $"super() argument 1 must be a type, not {PyOperations.TypeName(args[0])}");
            return PyValue.From(new PySuper(cls, args[1], ChargeSuperLookup));
        }
        if (args.Count != 0) throw Error("TypeError", "super() expects zero or two arguments");
        if (_scope.FirstPositionalParameter is not { } name)
            throw Error("RuntimeError", "super(): no arguments");
        if (!_scope.TryGetLocal(name, out var receiver))
            throw Error("RuntimeError", "super(): arg[0] deleted");
        if (_scope.DefiningClass is not { } classCell)
            throw Error("RuntimeError", "super(): __class__ cell not found");
        if (classCell.Value is not { } definingClass)
            throw Error("RuntimeError", "super(): empty __class__ cell");
        return PyValue.From(new PySuper(definingClass, receiver, ChargeSuperLookup));
    }

    private void ChargeSuperLookup() => Tick();

    private PyValue ExecuteBlock(IEnumerable<StatementSyntax> statements, bool returnLastExpression = false)
    {
        PyValue result = PyValue.None;
        foreach (StatementSyntax statement in statements)
        {
            Tick(statement);
            try
            {
                if (returnLastExpression && statement is ExpressionStatement expression)
                    result = Evaluate(expression.Expression);
                else
                    ExecuteStatement(statement);
            }
            catch (PythonException error)
            {
                ChainExceptionContext(error);
                if (error.Line == 0) throw AddLocation(error, statement);
                throw;
            }
            catch (ReturnSignal) { throw; }
            catch (BreakSignal) { throw; }
            catch (ContinueSignal) { throw; }
            catch (BudgetSignal limit) { limit.Error = AddLocation(limit.Error, statement); throw; }
            catch (Exception ex) { throw ChainExceptionContext(Error("RuntimeError", ex.Message, statement)); }
        }
        return result;
    }

    private void ExecuteStatement(StatementSyntax statement)
    {
        switch (statement)
        {
            case ExpressionStatement s: _ = Evaluate(s.Expression); break;
            case AssignmentStatement s:
                PyValue assigned = Evaluate(s.Value);
                foreach (ExpressionSyntax target in s.Targets) AssignTarget(target, assigned);
                break;
            case AugmentedAssignmentStatement s:
                if (s.Target is SubscriptExpression { Index: SliceExpression slice } sliced)
                {
                    PyValue container = Evaluate(sliced.Target);
                    var bounds = EvaluateSlice(slice);
                    PyValue oldValue = PyOperations.GetSlice(container, bounds);
                    PyOperations.SetSlice(container, bounds, Augment(oldValue, s), () => Tick(s));
                }
                else if (s.Target is SubscriptExpression subscript)
                {
                    PyValue container = Evaluate(subscript.Target);
                    PyValue index = Evaluate(subscript.Index);
                    PyValue oldValue = PyOperations.GetItem(container, index);
                    PyOperations.SetItem(container, index, Augment(oldValue, s));
                }
                else if (s.Target is AttributeExpression attribute)
                {
                    PyValue receiver = Evaluate(attribute.Target);
                    SetAttribute(receiver, attribute.Name, Augment(GetAttribute(receiver, attribute.Name), s));
                }
                else
                {
                    AssignTarget(s.Target, Augment(Evaluate(s.Target), s));
                }
                break;
            case IfStatement s:
                ExecuteBlock(Evaluate(s.Test).IsTruthy() ? s.Body : s.ElseBody);
                break;
            case WhileStatement s:
                bool broke = false;
                while (Evaluate(s.Test).IsTruthy())
                {
                    Tick(s);
                    try { ExecuteBlock(s.Body); }
                    catch (ContinueSignal) { continue; }
                    catch (BreakSignal) { broke = true; break; }
                }
                if (!broke) ExecuteBlock(s.ElseBody);
                break;
            case ForStatement s:
                broke = false;
                var iterable = Evaluate(s.Iterable);
                foreach (PyValue item in s.IsAsync ? IterateAsync(iterable, s) : PyOperations.Iterate(iterable))
                {
                    Tick(s);
                    AssignTarget(s.Target, item);
                    try { ExecuteBlock(s.Body); }
                    catch (ContinueSignal) { continue; }
                    catch (BreakSignal) { broke = true; break; }
                }
                if (!broke) ExecuteBlock(s.ElseBody);
                break;
            case FunctionDefinition s:
                var decorators = s.Decorators.Select(Evaluate).ToArray();
                PyScope closure = _scope.Closure;
                PyClassCell? definingClass = _scope.DefiningClass;
                var defaults = s.NamedDefaults.ToDictionary(pair => pair.Key, pair => Evaluate(pair.Value), StringComparer.Ordinal);
                var function = PyValue.From(new PyCallable(args => InvokeFunction(s, closure, definingClass, args, defaults), s.Name)
                {
                    IsPythonFunction = true,
                    Doc = s.Body.FirstOrDefault() is ExpressionStatement { Expression: ConstantExpression { Value: string doc } }
                        ? PyValue.From(doc) : PyValue.None,
                    InvokeWithKeywords = (args, keywords) => InvokeFunction(s, closure, definingClass, args, defaults, keywords)
                });
                _scope.Assign(s.Name, ApplyDecorators(function, decorators));
                break;
            case DeleteAttributeStatement s:
            {
                var target = Evaluate(s.Target.Target);
                if (target.Data is PyInstance instance) instance.DeleteAttribute(s.Target.Name);
                else if (target.Data is PyClass cls) cls.DeleteAttribute(s.Target.Name);
                else throw Error("AttributeError", $"'{PyOperations.TypeName(target)}' object does not support attribute deletion", s);
                break;
            }
            case MatchStatement s: ExecuteMatch(s); break;
            case ClassDefinition s: ExecuteClass(s); break;
            case TryStatement s: ExecuteTry(s); break;
            case WithStatement s: ExecuteWith(s); break;
            case RaiseStatement s: ExecuteRaise(s); break;
            case AssertStatement s:
                if (!Evaluate(s.Test).IsTruthy())
                {
                    var args = s.Message is null ? new PyTuple() : new PyTuple(Evaluate(s.Message));
                    throw PyExceptions.AssertionError(args, s);
                }
                break;
            case ReturnStatement s: throw new ReturnSignal(s.Value is null ? PyValue.None : Evaluate(s.Value));
            case BreakStatement: throw new BreakSignal();
            case ContinueStatement: throw new ContinueSignal();
            case PassStatement: break;
            case ScopeDeclarationStatement: break;
            case ImportStatement s:
                foreach (var (module, alias) in s.Imports)
                {
                    SyncCurrentModule();
                    PyDict imported = ImportModule(module);
                    _scope.Assign(alias ?? module.Split('.')[0], PyValue.From(alias is null && module.Contains('.') ? ImportModule(module.Split('.')[0]) : imported));
                }
                break;
            case FromImportStatement s:
                SyncCurrentModule();
                string resolved = ResolveRelative(s.Module, s.RelativeLevel, statement);
                PyDict source = ImportModule(resolved);
                foreach (var (name, alias) in s.Imports)
                {
                    try
                    {
                        PyValue value;
                        try { value = source[PyValue.From(name)]; }
                        catch (PythonException error) when (error.ErrorName == "KeyError")
                        { value = PyValue.From(ImportModule(resolved.Length == 0 ? name : resolved + "." + name)); }
                        _scope.Assign(alias ?? name, value);
                    }
                    catch (PythonException error) when (error.ErrorName == "ModuleNotFoundError" && error.FileName is null)
                    { throw Error("ImportError", $"cannot import name '{name}' from '{resolved}'", s); }
                }
                break;
            default: throw Error("NotImplementedError", $"unsupported statement {statement.GetType().Name}", statement);
        }
    }

    private static PyValue ApplyDecorators(PyValue definition, IReadOnlyList<PyValue> decorators)
    {
        for (int i = decorators.Count - 1; i >= 0; i--)
            definition = decorators[i].Call([definition]);
        return definition;
    }

    private void ExecuteClass(ClassDefinition statement)
    {
        var decorators = statement.Decorators.Select(Evaluate).ToArray();
        var bases = statement.Bases.Select(Evaluate).ToArray();
        if (bases.Any(value => value.Data is not PyClass))
            throw Error("TypeError", "class bases must be user-defined classes or object", statement);

        var enclosing = _scope;
        var classCell = new PyClassCell();
        var body = new PyScope(enclosing.Closure) { IsClassScope = true, DefiningClass = classCell };
        var declarations = FindDeclarations(statement.Body);
        foreach (string local in FindLocals(statement.Body)) body.DeclareLocal(local);
        foreach (string global in declarations.Globals) body.DeclareGlobal(global);
        foreach (string nonlocal in declarations.Nonlocals) body.DeclareNonlocal(nonlocal);
        body.Define("__module__", enclosing.ModuleName);
        _scope = body;
        try { ExecuteBlock(statement.Body); }
        finally { _scope = enclosing; }

        var cls = new PyClass(statement.Name, enclosing.ModuleName.ToPythonString(),
            bases.Length == 0 ? [PyClass.Object] : bases.Select(value => (PyClass)value.Data!).ToArray());
        cls.InitializeMembers(body.LocalValues);
        classCell.Value = cls;
        enclosing.Assign(statement.Name, ApplyDecorators(PyValue.From(cls), decorators));
    }

    private void ExecuteTry(TryStatement statement)
    {
        PythonException? previous = _activeException;
        PythonException? pending = null;
        bool budgetAborted = false;
        try
        {
            try { ExecuteBlock(statement.Body); }
            catch (PythonException error) when (statement.Handlers.Count > 0)
            {
                _activeException = error;
                if (!ExecuteHandler(statement.Handlers, error)) throw;
                return;
            }
            ExecuteBlock(statement.ElseBody);
        }
        catch (PythonException error) { pending = ChainExceptionContext(error); throw; }
        catch (BudgetSignal) { budgetAborted = true; throw; }
        finally
        {
            try
            {
                _activeException = pending ?? previous;
                // An interpreter limit stops all Python code, including cleanup suites.
                if (!budgetAborted) ExecuteBlock(statement.FinallyBody);
            }
            finally { _activeException = previous; }
        }
    }

    private void ExecuteWith(WithStatement statement)
    {
        string enterName = statement.IsAsync ? "__aenter__" : "__enter__";
        string exitName = statement.IsAsync ? "__aexit__" : "__exit__";
        var exits = new Stack<(PyValue Method, WithItem Item)>();
        ExceptionDispatchInfo? pending = null;
        try
        {
            foreach (var item in statement.Items)
            {
                Tick(item);
                PyValue manager = Evaluate(item.Context);
                PyValue enter, exit;
                if (statement.IsAsync)
                {
                    exit = GetContextMethod(manager, exitName, item, isAsync: true);
                    enter = GetContextMethod(manager, enterName, item, isAsync: true);
                }
                else
                {
                    enter = GetContextMethod(manager, enterName, item, isAsync: false);
                    exit = GetContextMethod(manager, exitName, item, isAsync: false);
                }
                PyValue entered = enter.Call([]);
                if (statement.IsAsync) entered = AwaitCoroutine(entered, AwaitContext.AsyncWithEnter);
                exits.Push((exit, item));
                if (item.Target is { } target) AssignTarget(target, entered);
            }
            ExecuteBlock(statement.Body);
        }
        catch (BudgetSignal) { throw; }
        catch (Exception error) { pending = CaptureWithException(error, statement); }

        while (exits.TryPop(out var exit))
        {
            PythonException? previous = _activeException;
            try
            {
                if (pending?.SourceException is PythonException error)
                {
                    _activeException = error;
                    var instance = PyExceptions.InstanceOf(error);
                    // ShonePy does not expose Python traceback objects.
                    if (CallExit(exit.Method, [PyValue.From(instance.Type), PyValue.From(instance), PyValue.None]).IsTruthy())
                        pending = null;
                }
                else
                {
                    // Normal completion and return/break/continue all use the same exit arguments.
                    CallExit(exit.Method, [PyValue.None, PyValue.None, PyValue.None]);
                }
            }
            catch (BudgetSignal) { throw; }
            catch (Exception error) { pending = CaptureWithException(error, exit.Item); }
            finally { _activeException = previous; }
        }
        pending?.Throw();

        PyValue CallExit(PyValue method, IReadOnlyList<PyValue> args)
        {
            PyValue result = method.Call(args);
            return statement.IsAsync ? AwaitCoroutine(result, AwaitContext.AsyncWithExit) : result;
        }
    }

    private PyValue GetContextMethod(PyValue manager, string name, WithItem item, bool isAsync)
    {
        try
        {
            if (manager.Data is not PyInstance instance) return GetAttribute(manager, name);
            if (instance.TryGetSpecial(name, out var method)) return method;
        }
        catch (PythonException error) when (error.ErrorName == "AttributeError" && (!isAsync || manager.Data is not PyInstance)) { }
        string protocol = isAsync ? "asynchronous context manager" : "context manager";
        throw Error("TypeError", $"'{PyOperations.TypeName(manager)}' object does not support the {protocol} protocol (missed {name} method)", item);
    }

    private ExceptionDispatchInfo CaptureWithException(Exception error, SyntaxNode location) =>
        ExceptionDispatchInfo.Capture(error switch
        {
            PythonException python => AddLocation(ChainExceptionContext(python), location),
            ReturnSignal or BreakSignal or ContinueSignal => error,
            _ => ChainExceptionContext(Error("RuntimeError", error.Message, location))
        });

    private bool ExecuteHandler(IEnumerable<ExceptClause> handlers, PythonException error)
    {
        var instance = PyExceptions.InstanceOf(error);
        foreach (var handler in handlers)
        {
            if (handler.Type is { } type && !PyExceptions.Matches(instance.Type, Evaluate(type), type)) continue;
            if (handler.Alias is { } alias) _scope.Assign(alias, PyValue.From(instance));
            try { ExecuteBlock(handler.Body); }
            finally { if (handler.Alias is { } name) _scope.ClearBinding(name); }
            return true;
        }
        return false;
    }

    private void ExecuteRaise(RaiseStatement statement)
    {
        if (statement.Value is null)
            throw _activeException ?? Error("RuntimeError", "No active exception to reraise", statement);

        PyValue value = Evaluate(statement.Value);
        PyValue? cause = statement.Cause is { } expression ? Evaluate(expression) : (PyValue?)null;
        var instance = value.Data switch
        {
            PyExceptionInstance error => error,
            PyExceptionClass cls => (PyExceptionInstance)cls.Create([]).Data!,
            _ => throw Error("TypeError", "exceptions must derive from BaseException", statement)
        };
        if (cause is { } explicitCause)
        {
            instance.SetCause(explicitCause.Data switch
            {
                null when explicitCause.Type == PyType.None => null,
                PyExceptionInstance error => error,
                PyExceptionClass cls => (PyExceptionInstance)cls.Create([]).Data!,
                _ => throw Error("TypeError", "exception causes must derive from BaseException", statement)
            });
        }
        throw new PythonException(instance.Type.Name, instance.ToString(), statement.Line, statement.Column) { Instance = instance };
    }

    private PythonException ChainExceptionContext(PythonException error)
    {
        // A propagated error keeps the context it acquired at its point of origin.
        if (error.ContextInitialized) return error;
        error.ContextInitialized = true;
        if (_activeException is { } active && !ReferenceEquals(error, active))
            PyExceptions.InstanceOf(error).ChainContext(PyExceptions.InstanceOf(active));
        return error;
    }

    private PyDict ImportModule(string name)
    {
        if (_managedModules.TryGetValue(name, out var module)) return module;
        try { module = PyModules.Import(name); }
        catch (PythonException error) when (error.ErrorName == "ModuleNotFoundError") { return _moduleLoader.Import(name); }
        _managedModules.Add(name, module);
        return module;
    }

    private PyDict ExecuteImportedModule(ModuleSyntax syntax, PyDict module)
    {
        PyScope previous = _scope;
        PyDict? previousModule = _currentModule;
        var moduleScope = new PyScope(null, _builtins);
        foreach (var item in module) moduleScope.Define((string)item.Key.Data!, item.Value);
        _scope = moduleScope;
        _currentModule = module;
        try
        {
            ExecuteBlock(syntax.Statements);
            SyncCurrentModule();
            return module;
        }
        catch (BudgetSignal limit) { limit.Error = AddFileName(limit.Error, syntax.FileName); throw; }
        catch (PythonException error) when (error.FileName is null) { throw AddFileName(error, syntax.FileName); }
        finally { _scope = previous; _currentModule = previousModule; }
    }

    private void SyncCurrentModule()
    {
        if (_currentModule is null) return;
        foreach (var pair in _scope.LocalValues) _currentModule[PyValue.From(pair.Key)] = pair.Value;
    }

    private string ResolveRelative(string module, int level, SyntaxNode location)
    {
        if (level == 0) return module;
        PyValue packageValue;
        try { packageValue = _scope.Get("__package__"); }
        catch (PythonException) { throw Error("ImportError", "attempted relative import with no known parent package", location); }
        string package = packageValue.Type == PyType.String ? (string)packageValue.Data! : string.Empty;
        string[] parts = package.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (level > parts.Length) throw Error("ImportError", "attempted relative import beyond top-level package", location);
        string prefix = string.Join('.', parts.Take(parts.Length - level + 1));
        return string.IsNullOrEmpty(module) ? prefix : string.IsNullOrEmpty(prefix) ? module : prefix + "." + module;
    }

    private PyValue InvokeFunction(FunctionDefinition function, PyScope closure, PyClassCell? definingClass, IReadOnlyList<PyValue> args,
        IReadOnlyDictionary<string, PyValue> defaults, IReadOnlyDictionary<string, PyValue>? keywords = null)
    {
        int positionalCount = function.KeywordOnlyStart;
        int acceptedPositional = Math.Min(args.Count, positionalCount);
        if (args.Count > positionalCount && function.VarArgName is null)
            throw Error("TypeError", $"{function.Name}() takes at most {positionalCount} positional arguments but {args.Count} were given");
        var bound = new PyValue?[function.Parameters.Count];
        for (int i = 0; i < acceptedPositional; i++) bound[i] = args[i];
        var extraKeywords = new PyDict();
        if (keywords is not null)
        {
            foreach (var (name, value) in keywords)
            {
                int index = function.Parameters.IndexOf(name);
                if (index >= 0 && index < function.PositionalOnlyCount)
                {
                    if (function.KwArgName is null) throw Error("TypeError", $"{function.Name}() got some positional-only arguments passed as keyword arguments: '{name}'");
                    extraKeywords[PyValue.From(name)] = value;
                    continue;
                }
                if (index < 0)
                {
                    if (function.KwArgName is null) throw Error("TypeError", $"{function.Name}() got an unexpected keyword argument '{name}'");
                    extraKeywords[PyValue.From(name)] = value;
                    continue;
                }
                if (bound[index].HasValue) throw Error("TypeError", $"{function.Name}() got multiple values for argument '{name}'");
                bound[index] = value;
            }
        }
        for (int i = 0; i < function.Parameters.Count; i++)
            if (!bound[i].HasValue && !defaults.ContainsKey(function.Parameters[i]))
                throw Error("TypeError", $"{function.Name}() missing required argument: '{function.Parameters[i]}'");

        var local = new PyScope(closure)
        {
            DefiningClass = definingClass,
            FirstPositionalParameter = positionalCount > 0 ? function.Parameters[0] : null
        };
        var declarations = FindDeclarations(function.Body);
        foreach (var name in declarations.Globals)
        {
            if (function.Parameters.Contains(name)) throw Error("SyntaxError", $"name '{name}' is parameter and global", function);
            local.DeclareGlobal(name);
        }
        foreach (var name in declarations.Nonlocals)
        {
            if (function.Parameters.Contains(name)) throw Error("SyntaxError", $"name '{name}' is parameter and nonlocal", function);
            if (!local.HasEnclosingBinding(name)) throw Error("SyntaxError", $"no binding for nonlocal '{name}' found", function);
            local.DeclareNonlocal(name);
        }
        foreach (string name in FindLocals(function.Body))
            if (!declarations.Globals.Contains(name) && !declarations.Nonlocals.Contains(name)) local.DeclareLocal(name);
        for (int i = 0; i < function.Parameters.Count; i++)
            local.Define(function.Parameters[i], bound[i] ?? defaults[function.Parameters[i]]);
        if (function.VarArgName is not null)
            local.Define(function.VarArgName, PyValue.From(new PyTuple(args.Skip(positionalCount))));
        if (function.KwArgName is not null)
            local.Define(function.KwArgName, PyValue.From(extraKeywords));
        if (function.IsAsync) return PyValue.From(new PyCoroutine(this, function.Name, () => ExecuteCoroutineBody(function, local)));
        if (function.IsGenerator) return CreateGenerator(function, local);
        return ExecuteFunctionBody(function, local);
    }

    private PyValue ExecuteFunctionBody(FunctionDefinition function, PyScope local)
    {
        CheckCallDepth(function);
        _callDepth++;
        PyScope previous = _scope;
        _scope = local;
        try
        {
            ExecuteBlock(function.Body);
            return PyValue.None;
        }
        catch (ReturnSignal result) { return result.Value; }
        finally { _scope = previous; _callDepth--; }
    }

    private PyValue Evaluate(ExpressionSyntax expression)
    {
        Tick(expression);
        try
        {
            return expression switch
            {
                NameExpression e => _scope.Get(e.Name),
                ConstantExpression e => PyValue.From(e.Value),
                UnaryExpression e => PyOperations.Unary(e.Operator, Evaluate(e.Operand)),
                BinaryExpression e => EvaluateBinary(e),
                ComparisonChainExpression e => EvaluateComparisonChain(e),
                ConditionalExpression e => Evaluate(Evaluate(e.Test).IsTruthy() ? e.WhenTrue : e.WhenFalse),
                CallExpression e => EvaluateCall(e),
                AttributeExpression e => GetAttribute(Evaluate(e.Target), e.Name),
                SubscriptExpression e => e.Index is SliceExpression slice
                    ? PyOperations.GetSlice(Evaluate(e.Target), EvaluateSlice(slice))
                    : PyOperations.GetItem(Evaluate(e.Target), Evaluate(e.Index)),
                ListExpression e => PyValue.From(new PyList(EvaluateSequenceItems(e.Items))),
                TupleExpression e => PyValue.From(new PyTuple(EvaluateSequenceItems(e.Items))),
                DictExpression e => EvaluateDict(e),
                SetExpression e => PyValue.From(new PySet(EvaluateSequenceItems(e.Items))),
                ListComprehensionExpression e => EvaluateListComprehension(e),
                DictComprehensionExpression e => EvaluateDictComprehension(e),
                DictUnpackingComprehensionExpression e => EvaluateDictUnpackingComprehension(e),
                SetComprehensionExpression e => EvaluateSetComprehension(e),
                GeneratorExpression e => EvaluateGeneratorExpression(e),
                AwaitExpression e => AwaitCoroutine(Evaluate(e.Operand)),
                YieldExpression => throw Error("NotImplementedError", "yield in this position is not supported", expression),
                _ => throw Error("NotImplementedError", $"unsupported expression {expression.GetType().Name}", expression)
            };
        }
        catch (PythonException ex) { throw AddLocation(ex, expression); }
        catch (ReturnSignal) { throw; }
        catch (BreakSignal) { throw; }
        catch (ContinueSignal) { throw; }
        catch (BudgetSignal limit) { limit.Error = AddLocation(limit.Error, expression); throw; }
        catch (Exception ex) { throw Error("RuntimeError", ex.Message, expression); }
    }

    private PyValue EvaluateComparisonChain(ComparisonChainExpression expression)
    {
        PyValue left = Evaluate(expression.Operands[0]);
        for (int i = 0; i < expression.Operators.Count; i++)
        {
            PyValue right = Evaluate(expression.Operands[i + 1]);
            string op = expression.Operators[i];
            if (!Compare(left, op, right)) return PyValue.False;
            left = right;
        }
        return PyValue.True;
    }

    private PyValue EvaluateBinary(BinaryExpression expression)
    {
        PyValue left = Evaluate(expression.Left);
        if (expression.Operator == "and") return left.IsTruthy() ? Evaluate(expression.Right) : left;
        if (expression.Operator == "or") return left.IsTruthy() ? left : Evaluate(expression.Right);
        return ApplyBinary(left, expression.Operator, Evaluate(expression.Right));
    }

    private static PyValue ApplyBinary(PyValue left, string op, PyValue right) => op switch
    {
        "and" or "or" => right,
        "==" or "!=" or "<" or "<=" or ">" or ">=" or "is" or "is not" or "in" or "not in" => PyValue.From(Compare(left, op, right)),
        _ => PyOperations.Binary(left, op, right)
    };

    private static bool Compare(PyValue left, string op, PyValue right) => op switch
    {
        "in" => PyOperations.Contains(right, left),
        "not in" => !PyOperations.Contains(right, left),
        "is" => PyOperations.Identity(left, right),
        "is not" => !PyOperations.Identity(left, right),
        _ => PyOperations.Compare(left, op, right)
    };

    private PyValue EvaluateDict(DictExpression expression)
    {
        var dict = new PyDict();
        foreach (var (key, value) in expression.Items)
        {
            if (key is null) MergeMapping(dict, value);
            else dict[Evaluate(key)] = Evaluate(value);
        }
        return PyValue.From(dict);
    }

    private PyValue EvaluateListComprehension(ListComprehensionExpression expression)
    {
        var list = new PyList();
        EvaluateComprehension(expression.Clauses, () =>
        {
            foreach (var value in EvaluateComprehensionElement(expression.Element, list: true)) list.Add(value);
        });
        return PyValue.From(list);
    }

    private PyValue EvaluateSetComprehension(SetComprehensionExpression expression)
    {
        var set = new PySet();
        EvaluateComprehension(expression.Clauses, () =>
        {
            foreach (var value in EvaluateComprehensionElement(expression.Element)) set.Add(value);
        });
        return PyValue.From(set);
    }

    private PyValue EvaluateDictComprehension(DictComprehensionExpression expression)
    {
        var dict = new PyDict();
        EvaluateComprehension(expression.Clauses, () => dict[Evaluate(expression.Key)] = Evaluate(expression.Value));
        return PyValue.From(dict);
    }

    private PyValue EvaluateDictUnpackingComprehension(DictUnpackingComprehensionExpression expression)
    {
        var dict = new PyDict();
        EvaluateComprehension(expression.Clauses, () => MergeMapping(dict, expression.Mapping));
        return PyValue.From(dict);
    }

    private void MergeMapping(PyDict destination, ExpressionSyntax expression)
    {
        var mapping = Evaluate(expression);
        if (mapping.Data is PyDict dict)
        {
            foreach (var (key, value) in dict)
            {
                Tick(expression);
                destination[key] = value;
            }
            return;
        }

        PyValue keys;
        try { keys = GetAttribute(mapping, "keys"); }
        catch (PythonException error) when (error.ErrorName == "AttributeError")
        { throw Error("TypeError", $"'{PyOperations.TypeName(mapping)}' object is not a mapping", expression); }
        // Python collects the keys before looking up the first value.
        var keyList = new List<PyValue>();
        foreach (var key in IterateUnpackingOperand(keys.Call([]), expression))
        {
            Tick(expression);
            keyList.Add(key);
        }
        foreach (var key in keyList)
        {
            Tick(expression);
            if (mapping.Data is not PyInstance instance || !instance.TryGetSpecial("__getitem__", out var getItem))
                throw Error("TypeError", $"'{PyOperations.TypeName(mapping)}' object is not subscriptable", expression);
            destination[key] = getItem.Call([key]);
        }
    }

    private IEnumerable<PyValue> EvaluateComprehensionElement(ExpressionSyntax element, bool list = false)
    {
        if (element is not StarredExpression star)
        {
            yield return Evaluate(element);
            yield break;
        }
        foreach (var item in IterateUnpackingOperand(Evaluate(star.Value), star, list))
        {
            Tick(star);
            yield return item;
        }
    }

    private IEnumerable<PyValue> IterateUnpackingOperand(PyValue value, SyntaxNode location, bool list = false)
    {
        if (value.Data is PyInstance instance)
        {
            if (instance.TryGetSpecial("__iter__", out var iter))
            {
                if (iter.Type == PyType.None)
                    throw Error("TypeError", $"'{PyOperations.TypeName(value)}' object is not iterable", location);
                var iterator = iter.Call([]);
                IEnumerable<PyValue> items = iterator.Data switch
                {
                    PyIterator cursor => cursor,
                    PyInstance custom when custom.Class.TryLookup("__next__", out _) => IterateCustomIterator(custom),
                    _ => throw Error("TypeError", $"iter() returned non-iterator of type '{PyOperations.TypeName(iterator)}'", location)
                };
                if (list) EvaluateUnpackingLengthHint(instance, location);
                return items;
            }
            if (instance.TryGetSpecial("__getitem__", out var getItem))
            {
                if (list) EvaluateUnpackingLengthHint(instance, location);
                return IterateSequenceProtocol(getItem);
            }
        }
        else
        {
            try
            {
                // Python list iterators keep their index even if the list changes during iteration.
                return value.Type == PyType.List ? PyOperations.GetIterator(value) : PyOperations.Iterate(value);
            }
            catch (PythonException error) when (error.ErrorName == "TypeError" && !list)
            { throw AddLocation(error, location); }
            catch (PythonException error) when (error.ErrorName == "TypeError") { }
        }
        var name = PyOperations.TypeName(value);
        throw Error("TypeError", list ? $"Value after * must be an iterable, not {name}" : $"'{name}' object is not iterable", location);

        static IEnumerable<PyValue> IterateCustomIterator(PyInstance iterator)
        {
            while (iterator.TryGetSpecial("__next__", out var next))
            {
                PyValue item;
                try { item = next.Call([]); }
                catch (PythonException error) when (error.ErrorName == "StopIteration") { yield break; }
                yield return item;
            }
        }

        static IEnumerable<PyValue> IterateSequenceProtocol(PyValue getItem)
        {
            for (int index = 0; ; index++)
            {
                PyValue item;
                try { item = getItem.Call([PyValue.From(index)]); }
                catch (PythonException error) when (error.ErrorName is "IndexError" or "StopIteration") { yield break; }
                yield return item;
            }
        }
    }

    private void EvaluateUnpackingLengthHint(PyInstance instance, SyntaxNode location)
    {
        // A list may use the hint for allocation. Observe the methods and validation
        // without preallocating a buffer based on an untrusted hint.
        if (TryLength("__len__")) return;
        TryLength("__length_hint__");

        bool TryLength(string name)
        {
            if (!instance.TryGetSpecial(name, out var method)) return false;
            PyValue length;
            try { length = method.Call([]); }
            catch (PythonException error) when (error.ErrorName == "TypeError") { return false; }
            if (length.Type == PyType.Boolean) return true;
            if (length.Data is not BigInt integer)
            {
                if (name == "__len__") return false;
                throw Error("TypeError", $"{name} must be an integer, not {PyOperations.TypeName(length)}", location);
            }
            if (integer < new BigInt(long.MinValue) || integer > new BigInt(long.MaxValue))
                throw Error("OverflowError", "cannot fit 'int' into an index-sized integer", location);
            if (integer.Sign < 0)
                throw Error("ValueError", $"{name}() should return >= 0", location);
            return true;
        }
    }

    private PyValue EvaluateGeneratorExpression(GeneratorExpression expression)
    {
        // Acquire the outer cursor in the enclosing scope, without advancing it.
        var outer = OpenComprehensionIterator(expression.Clauses[0], managed: true);
        var locals = CreateComprehensionScope(expression.Clauses);
        var cursor = RunComprehension(expression.Clauses, 0, outer, () => EvaluateComprehensionElement(expression.Element), managed: true);
        return CreateGenerator(expression, locals, cursor.GetEnumerator());
    }

    private void EvaluateComprehension(IReadOnlyList<ComprehensionClause> clauses, Action emit)
    {
        // Evaluate the outermost iterable before the comprehension's targets are in scope.
        using var outer = OpenComprehensionIterator(clauses[0], managed: false);
        PyScope enclosing = _scope;
        _scope = CreateComprehensionScope(clauses);
        try
        {
            foreach (var _ in RunComprehension(clauses, 0, outer, () => { emit(); return []; }, managed: false)) { }
        }
        finally { _scope = enclosing; }
    }

    private PyScope CreateComprehensionScope(IReadOnlyList<ComprehensionClause> clauses)
    {
        var local = new PyScope(_scope.Closure);
        foreach (var clause in clauses)
            VisitTargetNames(clause.Target, local.DeclareLocal);
        return local;
    }

    private IEnumerator<PyValue> OpenComprehensionIterator(ComprehensionClause clause, bool managed)
    {
        try
        {
            var iterable = Evaluate(clause.Iterable);
            if (clause.IsAsync)
                return ReadAsyncIterator(GetAsyncIterator(iterable, asyncFor: true), clause).GetEnumerator();
            return (managed ? PyOperations.GetIterator(iterable) : PyOperations.Iterate(iterable)).GetEnumerator();
        }
        catch (PythonException error) { throw AddLocation(error, clause.Iterable); }
    }

    private IEnumerable<PyValue> RunComprehension(IReadOnlyList<ComprehensionClause> clauses, int index,
        IEnumerator<PyValue> items, Func<IEnumerable<PyValue>> emit, bool managed)
    {
        var clause = clauses[index];
        while (items.MoveNext())
        {
            Tick(clause);
            AssignTarget(clause.Target, items.Current);
            if (!clause.Filters.All(filter => Evaluate(filter).IsTruthy())) continue;
            if (index + 1 == clauses.Count)
            {
                foreach (var value in emit()) yield return value;
            }
            else
            {
                using var inner = OpenComprehensionIterator(clauses[index + 1], managed);
                foreach (var value in RunComprehension(clauses, index + 1, inner, emit, managed)) yield return value;
            }
        }
    }

    private PyValue EvaluateCall(CallExpression expression)
    {
        PyValue callable = Evaluate(expression.Function);
        var arguments = new List<PyValue>();
        var keywords = new Dictionary<string, PyValue>(StringComparer.Ordinal);
        var ordered = expression.OrderedArguments.Count > 0
            ? expression.OrderedArguments
            : expression.Arguments.Select(value => new CallArgument(CallArgumentKind.Positional, value))
                .Concat(expression.Keywords.Select(pair => new CallArgument(CallArgumentKind.Keyword, pair.Value, pair.Name))).ToArray();

        // Python evaluates positional arguments and * expansions before any keyword expressions.
        foreach (CallArgument argument in ordered.OrderBy(argument =>
                     argument.Kind is CallArgumentKind.Keyword or CallArgumentKind.MappingExpansion))
        {
            PyValue value = Evaluate(argument.Value);
            switch (argument.Kind)
            {
                case CallArgumentKind.Positional:
                    if (keywords.Count > 0) throw Error("TypeError", "positional argument follows keyword argument", argument.Value);
                    arguments.Add(value);
                    break;
                case CallArgumentKind.Keyword:
                    AddKeyword(argument.Name!, value, argument.Value);
                    break;
                case CallArgumentKind.IterableExpansion:
                    try { arguments.AddRange(PyOperations.Iterate(value)); }
                    catch (PythonException error) when (error.ErrorName == "TypeError")
                    { throw Error("TypeError", $"argument after * must be an iterable, not {PyOperations.TypeName(value)}", argument.Value); }
                    break;
                case CallArgumentKind.MappingExpansion:
                    if (value.Type != PyType.Dictionary)
                        throw Error("TypeError", $"argument after ** must be a mapping, not {PyOperations.TypeName(value)}", argument.Value);
                    foreach (var pair in (PyDict)value.Data!)
                    {
                        if (pair.Key.Type != PyType.String)
                            throw Error("TypeError", "keywords must be strings", argument.Value);
                        AddKeyword((string)pair.Key.Data!, pair.Value, argument.Value);
                    }
                    break;
            }
        }

        return callable.Call(arguments, keywords);

        void AddKeyword(string name, PyValue value, SyntaxNode location)
        {
            if (!keywords.TryAdd(name, value))
                throw Error("TypeError", $"got multiple values for keyword argument '{name}'", location);
        }
    }

    private PyValue GetAttribute(PyValue target, string name)
    {
        if (target.Data is PyInstance instance) return instance.GetAttribute(name);
        if (target.Data is PyClass userClass) return userClass.GetAttribute(name);
        if (target.Data is PySuper proxy)
            return name == "__class__" ? _builtins.Get("super") : proxy.GetAttribute(name, ChargeSuperLookup);
        if (target.Data is PyDescriptor descriptor) return descriptor.GetAttribute(name);
        if (target.Data is PyDateTimeModule.Value temporal) return temporal.GetAttribute(name);
        if (target.Data is PyRange range) return range.GetAttribute(name);
        if (target.Data is PyIterator { Name: "generator" } generator)
        {
            return name switch
            {
                "__iter__" => PyValue.From(new PyCallable(args => args.Count == 0 ? target : throw Error("TypeError", "__iter__() takes no arguments"), name)),
                "__next__" => PyValue.From(new PyCallable(args => args.Count == 0 ? PyBuiltins.Call("next", [target]) : throw Error("TypeError", "__next__() takes no arguments"), name)),
                "send" or "throw" or "close" => PyValue.From(new PyCallable(_ => throw Error("NotImplementedError", $"generator.{name}() is not supported"), name)),
                _ => throw Error("AttributeError", $"'generator' object has no attribute '{name}'")
            };
        }
        if (target.Data is PySet set) return set.GetAttribute(name);
        if (target.Data is PyCallable { Attributes: { } attributes } && attributes.TryGetValue(name, out var member)) return member;
        if (target.Data is PyCallable { IsPythonFunction: true } function && name == "__doc__") return function.Doc;
        if (target.Data is PyExceptionInstance error)
        {
            if (name == "args") return PyValue.From(error.Args);
            if (name == "value" && error.Type.Name == "StopIteration")
                return error.Args.Count == 0 ? PyValue.None : error.Args[0];
            if (name == "__class__") return PyValue.From(error.Type);
            if (name == "__context__") return error.Context is { } context ? PyValue.From(context) : PyValue.None;
            if (name == "__cause__") return error.Cause is { } cause ? PyValue.From(cause) : PyValue.None;
            if (name == "__suppress_context__") return PyValue.From(error.SuppressContext);
        }
        if (target.Data is PyExceptionClass cls && name == "__name__") return PyValue.From(cls.Name);
        if (target.Data is PyCounter counter)
        {
            if (counter.GetMethod(name) is { } method) return PyValue.From(method);
            throw Error("AttributeError", $"'Counter' object has no attribute '{name}'");
        }
        if (target.Type == PyType.Dictionary)
        {
            var dictionary = (PyDict)target.Data!;
            PyValue key = PyValue.From(name);
            if (dictionary.ContainsKey(key)) return dictionary[key];
        }
        if (target.Type == PyType.Dictionary && name == "get")
            return PyValue.From(new PyCallable(args => args.Count is 1 or 2 ? ((PyDict)target.Data!)[args[0]] : throw Error("TypeError", "get() expected 1 or 2 arguments"), "get"));
        if (target.Type == PyType.List && name == "append")
            return PyValue.From(new PyCallable(args => { if (args.Count != 1) throw Error("TypeError", "append() takes exactly one argument"); ((PyList)target.Data!).Add(args[0]); return PyValue.None; }, "append"));
        throw Error("AttributeError", $"'{target.Type.ToString().ToLowerInvariant()}' object has no attribute '{name}'");
    }

    private void SetAttribute(PyValue receiver, string name, PyValue value)
    {
        if (receiver.Data is PyInstance instance) instance.SetAttribute(name, value);
        else if (receiver.Data is PyClass cls) cls.SetAttribute(name, value);
        else throw Error("AttributeError", $"'{PyOperations.TypeName(receiver)}' object does not support assignment to '{name}'");
    }

    internal static (HashSet<string> Globals, HashSet<string> Nonlocals) FindDeclarations(IEnumerable<StatementSyntax> statements)
    {
        var globals = new HashSet<string>(StringComparer.Ordinal);
        var nonlocals = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case ScopeDeclarationStatement declaration:
                    foreach (string name in declaration.Names) (declaration.IsGlobal ? globals : nonlocals).Add(name);
                    break;
                case IfStatement conditional:
                    Merge(FindDeclarations(conditional.Body)); Merge(FindDeclarations(conditional.ElseBody)); break;
                case WhileStatement loop:
                    Merge(FindDeclarations(loop.Body)); Merge(FindDeclarations(loop.ElseBody)); break;
                case ForStatement loop:
                    Merge(FindDeclarations(loop.Body)); Merge(FindDeclarations(loop.ElseBody)); break;
                case TryStatement attempt:
                    foreach (var suite in attempt.Suites()) Merge(FindDeclarations(suite));
                    break;
                case WithStatement context:
                    Merge(FindDeclarations(context.Body));
                    break;
                case MatchStatement match:
                    foreach (var clause in match.Cases) Merge(FindDeclarations(clause.Body));
                    break;
            }
        }
        if (globals.Overlaps(nonlocals)) throw new PythonException("SyntaxError", "name declared both global and nonlocal");
        return (globals, nonlocals);

        void Merge((HashSet<string> Globals, HashSet<string> Nonlocals) found)
        {
            globals.UnionWith(found.Globals);
            nonlocals.UnionWith(found.Nonlocals);
        }
    }

    internal static HashSet<string> FindLocals(IEnumerable<StatementSyntax> statements)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (StatementSyntax statement in statements)
        {
            switch (statement)
            {
                case ScopeDeclarationStatement: break;
                case AssignmentStatement assignment:
                    foreach (ExpressionSyntax target in assignment.Targets) AddTarget(target);
                    break;
                case AugmentedAssignmentStatement augmented: AddTarget(augmented.Target); break;
                case FunctionDefinition function: names.Add(function.Name); break;
                case ClassDefinition cls: names.Add(cls.Name); break;
                case ImportStatement import:
                    foreach (var (module, alias) in import.Imports) names.Add(alias ?? module.Split('.')[0]);
                    break;
                case FromImportStatement import:
                    foreach (var (name, alias) in import.Imports) names.Add(alias ?? name);
                    break;
                case ForStatement loop:
                    AddTarget(loop.Target);
                    names.UnionWith(FindLocals(loop.Body));
                    names.UnionWith(FindLocals(loop.ElseBody));
                    break;
                case IfStatement conditional:
                    names.UnionWith(FindLocals(conditional.Body));
                    names.UnionWith(FindLocals(conditional.ElseBody));
                    break;
                case WhileStatement loop:
                    names.UnionWith(FindLocals(loop.Body));
                    names.UnionWith(FindLocals(loop.ElseBody));
                    break;
                case TryStatement attempt:
                    foreach (var handler in attempt.Handlers)
                        if (handler.Alias is { } name) names.Add(name);
                    foreach (var suite in attempt.Suites()) names.UnionWith(FindLocals(suite));
                    break;
                case WithStatement context:
                    foreach (var item in context.Items)
                        if (item.Target is { } target) AddTarget(target);
                    names.UnionWith(FindLocals(context.Body));
                    break;
                case MatchStatement match:
                    foreach (var clause in match.Cases)
                    {
                        names.UnionWith(clause.Captures);
                        names.UnionWith(FindLocals(clause.Body));
                    }
                    break;
            }
        }
        return names;

        void AddTarget(ExpressionSyntax target) => VisitTargetNames(target, name => names.Add(name));
    }

    private static void VisitTargetNames(ExpressionSyntax target, Action<string> visit)
    {
        switch (target)
        {
            case NameExpression name:
                visit(name.Name);
                break;
            case StarredExpression star:
                VisitTargetNames(star.Value, visit);
                break;
            case TupleExpression tuple:
                foreach (var item in tuple.Items) VisitTargetNames(item, visit);
                break;
            case ListExpression list:
                foreach (var item in list.Items) VisitTargetNames(item, visit);
                break;
        }
    }

    private IEnumerable<PyValue> EvaluateSequenceItems(IEnumerable<ExpressionSyntax> items)
    {
        foreach (var item in items)
        {
            if (item is not StarredExpression star)
            {
                yield return Evaluate(item);
                continue;
            }

            foreach (var value in PyOperations.Iterate(Evaluate(star.Value)))
            {
                Tick(star);
                yield return value;
            }
        }
    }

    private PyValue Augment(PyValue left, AugmentedAssignmentStatement statement)
    {
        PyValue right = Evaluate(statement.Value);
        if (left.Data is PySet leftSet && right.Data is PySet rightSet && PySet.IsSetOperator(statement.Operator))
        {
            leftSet.Operate(statement.Operator, rightSet, inPlace: true);
            return left;
        }
        if (statement.Operator != "+" || left.Data is not PyList list)
            return PyOperations.Binary(left, statement.Operator, right);

        IEnumerable<PyValue> items = ReferenceEquals(list, right.Data) ? list.ToArray() : PyOperations.Iterate(right);
        foreach (var item in items)
        {
            Tick(statement);
            list.Add(item);
        }
        return left;
    }

    private PyOperations.Slice EvaluateSlice(SliceExpression slice) => new(
        slice.Start is null ? (PyValue?)null : Evaluate(slice.Start),
        slice.Stop is null ? (PyValue?)null : Evaluate(slice.Stop),
        slice.Step is null ? (PyValue?)null : Evaluate(slice.Step));

    private void AssignTarget(ExpressionSyntax target, PyValue value)
    {
        switch (target)
        {
            case NameExpression name: _scope.Assign(name.Name, value); break;
            case AttributeExpression attribute: SetAttribute(Evaluate(attribute.Target), attribute.Name, value); break;
            case SubscriptExpression { Index: SliceExpression slice } sub:
                PyOperations.SetSlice(Evaluate(sub.Target), EvaluateSlice(slice), value, () => Tick(sub));
                break;
            case SubscriptExpression sub: PyOperations.SetItem(Evaluate(sub.Target), Evaluate(sub.Index), value); break;
            case TupleExpression tuple: Unpack(tuple.Items, value, target); break;
            case ListExpression list: Unpack(list.Items, value, target); break;
            default: throw Error("SyntaxError", "cannot assign to expression", target);
        }
    }

    private void Unpack(IReadOnlyList<ExpressionSyntax> targets, PyValue value, SyntaxNode location)
    {
        using var items = PyOperations.Iterate(value).GetEnumerator();
        var values = new PyValue[targets.Count];
        int starIndex = -1;
        for (int i = 0; i < targets.Count; i++)
            if (targets[i] is StarredExpression) { starIndex = i; break; }

        if (starIndex >= 0)
        {
            int minimum = targets.Count - 1;
            for (int i = 0; i < starIndex; i++)
            {
                if (!items.MoveNext()) throw TooFew(i);
                values[i] = items.Current;
            }

            var middle = new PyList();
            while (items.MoveNext())
            {
                Tick(location);
                middle.Add(items.Current);
            }
            int trailing = minimum - starIndex;
            if (middle.Count < trailing) throw TooFew(starIndex + middle.Count);
            int split = middle.Count - trailing;
            for (int i = 0; i < trailing; i++) values[starIndex + 1 + i] = middle[split + i];
            middle.ReplaceRange(split, trailing, []);
            values[starIndex] = PyValue.From(middle);
            for (int i = 0; i < targets.Count; i++)
                AssignTarget(targets[i] is StarredExpression star ? star.Value : targets[i], values[i]);
            return;

            PythonException TooFew(int count) => Error("ValueError", $"not enough values to unpack (expected at least {minimum}, got {count})", location);
        }
        for (int i = 0; i < targets.Count; i++)
        {
            if (!items.MoveNext())
                throw Error("ValueError", $"not enough values to unpack (expected {targets.Count}, got {i})", location);
            values[i] = items.Current;
        }
        if (items.MoveNext())
            throw Error("ValueError", $"too many values to unpack (expected {targets.Count})", location);
        for (int i = 0; i < targets.Count; i++)
            AssignTarget(targets[i], values[i]);
    }

    private void CheckCallDepth(SyntaxNode node)
    {
        if (_callDepth >= _options.MaximumCallDepth)
            throw new BudgetSignal(Error("RecursionError", "maximum recursion depth exceeded", node));
    }

    private void Tick(SyntaxNode? node = null)
    {
        if (_steps >= _options.MaximumSteps) throw new BudgetSignal(Error("RuntimeError", "maximum execution steps exceeded", node));
        _steps++;
    }
    private PythonException Error(string name, string message, SyntaxNode? node = null) => new(name, message, node?.Line ?? 0, node?.Column ?? 0);
    private static PythonException AddLocation(PythonException ex, SyntaxNode node) =>
        ex.Line != 0 ? ex : ex.WithLocation(node.Line, node.Column, ex.FileName);
    private static PythonException AddFileName(PythonException ex, string? fileName) =>
        ex.FileName is not null || fileName is null ? ex : ex.WithLocation(ex.Line, ex.Column, fileName);
    private sealed class BudgetSignal(PythonException error) : Exception
    {
        public PythonException Error { get; set; } = error;
    }
    private sealed class ReturnSignal(PyValue value) : Exception { public PyValue Value { get; } = value; }
    private sealed class BreakSignal : Exception;
    private sealed class ContinueSignal : Exception;
}
