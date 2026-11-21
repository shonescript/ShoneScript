// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneJs.Parsing;
using ShoneLisp.Parsing;

namespace ShoneLisp.Runtime;

internal sealed class LispEnvironment(LispEnvironment? parent = null)
{
    private readonly Dictionary<string, LispValue> _bindings = new(StringComparer.Ordinal);

    public void Define(string name, LispValue value) => _bindings[name] = value;

    public bool TryLookup(string name, out LispValue value)
    {
        for (LispEnvironment? scope = this; scope is not null; scope = scope.Parent)
            if (scope._bindings.TryGetValue(name, out value!))
                return true;
        value = LispValue.Unspecified;
        return false;
    }

    public LispEnvironment? Resolve(string name)
    {
        for (LispEnvironment? scope = this; scope is not null; scope = scope.Parent)
            if (scope._bindings.ContainsKey(name))
                return scope;
        return null;
    }

    private LispEnvironment? Parent { get; } = parent;
}

internal abstract record LispProcedure;
internal sealed record Closure(string[] Parameters, LispExpression[] Body, LispEnvironment Environment, string? FileName) : LispProcedure;
internal sealed record Builtin(string Name, int Minimum, int Maximum, Func<CallSite, LispValue[], LispValue> Invoke) : LispProcedure;

internal sealed class EvaluationBudget(int remainingSteps, int maximumDepth, TextWriter output)
{
    private int _remainingSteps = remainingSteps;
    public TextWriter Output { get; } = output;

    public void Step(CallSite site)
    {
        if (_remainingSteps == 0)
            throw site.Error("Maximum execution steps exceeded.");
        _remainingSteps--;
    }

    public void Enter(CallSite site, int depth)
    {
        if (depth > maximumDepth)
            throw site.Error("Maximum evaluation depth exceeded.");
        Step(site);
    }
}

internal readonly record struct CallSite(SourceSpan Span, string? FileName, EvaluationBudget Budget)
{
    public LispRuntimeException Error(string message) => new(message, Span, FileName);
    public void Step() => Budget.Step(this);
}

internal sealed class LispInterpreter
{
    private readonly LispEnvironment _globals = new();

    public LispInterpreter() => LispBuiltins.Install(_globals);

    public void SetGlobal(string name, LispValue value) => _globals.Define(name, value);
    public LispValue GetGlobal(string name) => _globals.TryLookup(name, out var value)
        ? value
        : throw new LispRuntimeException($"Unbound symbol '{name}'.", new(new(0, 1, 1), new(0, 1, 1)));

    public LispValue Execute(ParsedLispScript script, LispOptions options)
    {
        int steps = options.MaximumSteps;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(steps, nameof(options.MaximumSteps));
        int depth = options.MaximumEvaluationDepth;
        LispOptions.ValidateDepth(depth, nameof(options.MaximumEvaluationDepth));
        var budget = new EvaluationBudget(steps, depth, options.Output);
        return EvaluateSequence(script.Expressions, _globals, script.FileName, budget, 1);
    }

    private static LispValue EvaluateSequence(ReadOnlySpan<LispExpression> expressions, LispEnvironment scope,
        string? fileName, EvaluationBudget budget, int depth)
    {
        var result = LispValue.Unspecified;
        foreach (var expression in expressions)
            result = Evaluate(expression, scope, fileName, budget, depth);
        return result;
    }

    private static LispValue Evaluate(LispExpression expression, LispEnvironment scope,
        string? fileName, EvaluationBudget budget, int depth)
    {
        var site = new CallSite(expression.Span, fileName, budget);
        budget.Enter(site, depth);
        if (expression is AtomExpression atom)
        {
            if (atom.Value.Kind != LispValueKind.Symbol)
                return atom.Value;
            string name = atom.Value.AsSymbol();
            return scope.TryLookup(name, out var value) ? value : throw site.Error($"Unbound symbol '{name}'.");
        }

        var list = (ListExpression)expression;
        if (list.Tail is not null || list.Items.Length == 0)
            throw site.Error("An application must be a non-empty proper list.");
        var args = list.Items.AsSpan(1);
        int childDepth = depth + 1;
        if (list.Items[0] is AtomExpression { Value.Kind: LispValueKind.Symbol } head)
        {
            switch (head.Value.AsSymbol())
            {
                case "quote":
                    RequireArity(site, "quote", args.Length, 1, 1);
                    return Quote(args[0], fileName, budget, childDepth);
                case "if":
                    RequireArity(site, "if", args.Length, 2, 3);
                    var test = Evaluate(args[0], scope, fileName, budget, childDepth);
                    if (test != LispValue.False)
                        return Evaluate(args[1], scope, fileName, budget, childDepth);
                    return args.Length == 3 ? Evaluate(args[2], scope, fileName, budget, childDepth) : LispValue.Unspecified;
                case "begin":
                    return EvaluateSequence(args, scope, fileName, budget, childDepth);
                case "define":
                    RequireArity(site, "define", args.Length, 2, int.MaxValue);
                    if (args[0] is ListExpression signature)
                    {
                        if (signature.Items.Length == 0)
                            throw Location(signature, site).Error("Expected a function name in 'define'.");
                        string name = RequireSymbol(signature.Items[0], site);
                        scope.Define(name, CreateClosure(signature, 1, args[1..], scope, site));
                    }
                    else
                    {
                        RequireArity(site, "define", args.Length, 2, 2);
                        string name = RequireSymbol(args[0], site);
                        scope.Define(name, Evaluate(args[1], scope, fileName, budget, childDepth));
                    }
                    return LispValue.Unspecified;
                case "lambda":
                    RequireArity(site, "lambda", args.Length, 2, int.MaxValue);
                    var parameters = RequireList(args[0], site, "Expected a proper parameter list in 'lambda'.");
                    return CreateClosure(parameters, 0, args[1..], scope, site);
                case "set!":
                    RequireArity(site, "set!", args.Length, 2, 2);
                    string target = RequireSymbol(args[0], site);
                    var bindingScope = scope.Resolve(target) ?? throw Location(args[0], site).Error($"Unbound symbol '{target}'.");
                    bindingScope.Define(target, Evaluate(args[1], scope, fileName, budget, childDepth));
                    return LispValue.Unspecified;
                case "let":
                    RequireArity(site, "let", args.Length, 2, int.MaxValue);
                    return EvaluateLet(args, scope, site, childDepth);
            }
        }

        var callable = Evaluate(list.Items[0], scope, fileName, budget, childDepth);
        if (callable.Kind != LispValueKind.Procedure)
            throw Location(list.Items[0], site).Error("Expected a procedure in operator position.");
        var values = new LispValue[args.Length];
        for (int i = 0; i < args.Length; i++)
            values[i] = Evaluate(args[i], scope, fileName, budget, childDepth);
        switch (callable.AsProcedure())
        {
            case Builtin builtin:
                RequireArity(site, builtin.Name, values.Length, builtin.Minimum, builtin.Maximum);
                return builtin.Invoke(site, values);
            case Closure closure:
                RequireArity(site, "procedure", values.Length, closure.Parameters.Length, closure.Parameters.Length);
                var local = new LispEnvironment(closure.Environment);
                for (int i = 0; i < closure.Parameters.Length; i++)
                    local.Define(closure.Parameters[i], values[i]);
                return EvaluateSequence(closure.Body, local, closure.FileName, budget, childDepth);
            default:
                throw new InvalidOperationException();
        }
    }

    private static LispValue EvaluateLet(ReadOnlySpan<LispExpression> args, LispEnvironment scope, CallSite site, int depth)
    {
        var bindings = RequireList(args[0], site, "Expected a proper binding list in 'let'.");
        var names = new string[bindings.Items.Length];
        var initializers = new LispExpression[names.Length];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < names.Length; i++)
        {
            var binding = RequireList(bindings.Items[i], site, "Expected a binding of the form (name value).");
            if (binding.Items.Length != 2)
                throw Location(binding, site).Error("Expected a binding of the form (name value).");
            var name = RequireSymbol(binding.Items[0], site);
            if (!seen.Add(name))
                throw Location(binding.Items[0], site).Error($"Duplicate binding '{name}'.");
            names[i] = name;
            initializers[i] = binding.Items[1];
        }
        var local = new LispEnvironment(scope);
        for (int i = 0; i < names.Length; i++)
            local.Define(names[i], Evaluate(initializers[i], scope, site.FileName, site.Budget, depth));
        return EvaluateSequence(args[1..], local, site.FileName, site.Budget, depth);
    }

    private static LispValue CreateClosure(ListExpression parameters, int offset, ReadOnlySpan<LispExpression> body,
        LispEnvironment scope, CallSite site)
    {
        if (parameters.Tail is not null)
            throw Location(parameters, site).Error("Only fixed-arity parameter lists are supported.");
        var names = new string[parameters.Items.Length - offset];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < names.Length; i++)
        {
            var parameter = parameters.Items[i + offset];
            names[i] = RequireSymbol(parameter, site);
            if (!seen.Add(names[i]))
                throw Location(parameter, site).Error($"Duplicate parameter '{names[i]}'.");
        }
        return LispValue.FromProcedure(new Closure(names, body.ToArray(), scope, site.FileName));
    }

    private static LispValue Quote(LispExpression expression, string? fileName, EvaluationBudget budget, int depth)
    {
        budget.Enter(new(expression.Span, fileName, budget), depth);
        if (expression is AtomExpression atom)
            return atom.Value;
        var list = (ListExpression)expression;
        var tail = list.Tail is null ? LispValue.EmptyList : Quote(list.Tail, fileName, budget, depth + 1);
        for (int i = list.Items.Length - 1; i >= 0; i--)
            tail = LispValue.Cons(Quote(list.Items[i], fileName, budget, depth + 1), tail);
        return tail;
    }

    private static void RequireArity(CallSite site, string name, int actual, int minimum, int maximum)
    {
        if (actual >= minimum && actual <= maximum)
            return;
        string expected = minimum == maximum ? $"{minimum}" : maximum == int.MaxValue ? $"at least {minimum}" : $"{minimum} to {maximum}";
        throw site.Error($"'{name}' expects {expected} argument(s), got {actual}.");
    }

    private static CallSite Location(LispExpression expression, CallSite site) => site with { Span = expression.Span };
    private static ListExpression RequireList(LispExpression expression, CallSite site, string message) =>
        expression is ListExpression { Tail: null } list ? list : throw Location(expression, site).Error(message);
    private static string RequireSymbol(LispExpression expression, CallSite site) =>
        expression is AtomExpression { Value.Kind: LispValueKind.Symbol } atom
            ? atom.Value.AsSymbol()
            : throw Location(expression, site).Error("Expected a symbol.");
}
