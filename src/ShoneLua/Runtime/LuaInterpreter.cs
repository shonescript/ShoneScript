// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneJs.Parsing;
using ShoneLua.Parsing;
using Index = ShoneLua.Parsing.Index;

namespace ShoneLua.Runtime;

internal sealed class LuaInterpreter
{
    private readonly LuaOptions _options;
    private readonly LuaScope _globals = new();

    public LuaInterpreter(LuaOptions options)
    {
        _options = options;
        _globals.Set("print", LuaValue.FromFunction(new LuaNativeFunction(static (output, values) =>
        {
            for (var i = 0; i < values.Length; i++)
            {
                if (i != 0) output.Write('\t');
                output.Write(values[i].ToString());
            }
            output.WriteLine();
            return LuaValue.Nil;
        })));
    }

    public LuaValue GetGlobal(string name) => _globals.Get(name);
    public void SetGlobal(string name, LuaValue value) => _globals.Set(name, value);

    public LuaExecutionResult Execute(ParsedLuaScript script)
    {
        var steps = _options.MaximumSteps;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(steps, nameof(_options.MaximumSteps));
        var depth = LuaOptions.ValidateDepth(_options.MaximumEvaluationDepth, nameof(_options.MaximumEvaluationDepth));
        var callDepth = LuaOptions.ValidateDepth(_options.MaximumCallDepth, nameof(_options.MaximumCallDepth));
        return new Execution(_options.Output, script.FileName, steps, depth, callDepth).Run(script.Syntax, _globals);
    }

    private enum Flow { Continue, Break, Return }
    private readonly record struct Completion(Flow Flow, LuaExecutionResult Result = default);

    private readonly record struct Destination(LuaScope? Scope, string? Name, LuaValue Target, LuaValue Key, SourceSpan Span);

    private sealed class Execution(TextWriter output, string? fileName, int steps, int maximumDepth, int maximumCallDepth)
    {
        private int _remaining = steps;
        private int _depth;
        private int _callDepth;
        private string? _fileName = fileName;

        public LuaExecutionResult Run(Block block, LuaScope globals) => ExecuteBlock(block, globals).Result;

        private Completion ExecuteBlock(Block block, LuaScope scope)
        {
            Enter(block.Span);
            try
            {
                foreach (var statement in block.Statements)
                {
                    var completion = ExecuteStatement(statement, ref scope);
                    if (completion.Flow != Flow.Continue) return completion;
                }
                return default;
            }
            finally { _depth--; }
        }

        private Completion ExecuteStatement(Statement statement, ref LuaScope scope)
        {
            Tick(statement.Span);
            switch (statement)
            {
                case Local local:
                    var values = EvaluateList(local.Values, scope);
                    scope = Bind(scope, local.Names, values, local.Span);
                    return default;
                case LocalFunction function:
                    scope = new LuaScope(scope);
                    scope.Set(function.Name, Close(function.Function, scope));
                    return default;
                case Assignment assignment:
                    var destinations = new Destination[assignment.Targets.Count];
                    for (var i = 0; i < destinations.Length; i++) destinations[i] = Locate(assignment.Targets[i], scope);
                    var assigned = EvaluateList(assignment.Values, scope);
                    for (var i = destinations.Length - 1; i >= 0; i--) Store(destinations[i], At(assigned, i));
                    return default;
                case CallStatement call:
                    Evaluate(call.Call, scope);
                    return default;
                case IfStatement conditional:
                    foreach (var branch in conditional.Branches)
                        if (Evaluate(branch.Condition, scope).IsTruthy) return ExecuteBlock(branch.Body, scope);
                    return conditional.Else is null ? default : ExecuteBlock(conditional.Else, scope);
                case DoStatement nested:
                    return ExecuteBlock(nested.Body, scope);
                case WhileStatement loop:
                    while (Evaluate(loop.Condition, scope).IsTruthy)
                    {
                        var result = ExecuteBlock(loop.Body, scope);
                        if (result.Flow == Flow.Break) break;
                        if (result.Flow == Flow.Return) return result;
                    }
                    return default;
                case NumericFor loop:
                    return ExecuteFor(loop, scope);
                case ReturnStatement returned:
                    var results = EvaluateList(returned.Values, scope);
                    return new Completion(Flow.Return, new LuaExecutionResult(results.Length != 0, At(results, 0)));
                case BreakStatement:
                    return new Completion(Flow.Break);
                default:
                    throw new InvalidOperationException("Unknown Lua statement.");
            }
        }

        private Completion ExecuteFor(NumericFor loop, LuaScope scope)
        {
            var initial = Evaluate(loop.Start, scope);
            var boundary = Evaluate(loop.Limit, scope);
            var stride = loop.Step is null ? LuaValue.FromNumber(1) : Evaluate(loop.Step, scope);
            var start = Number(initial, loop.Start.Span);
            var limit = Number(boundary, loop.Limit.Span);
            var step = Number(stride, loop.Step?.Span ?? loop.Span);
            if (step == 0) throw Error("Numeric for step must not be zero.", loop.Step?.Span ?? loop.Span);
            if (double.IsNaN(step)) return default;
            for (var control = start; step > 0 ? control <= limit : control >= limit; control += step)
            {
                Tick(loop.Span);
                var iteration = new LuaScope(scope);
                iteration.Set(loop.Name, LuaValue.FromNumber(control));
                var result = ExecuteBlock(loop.Body, iteration);
                if (result.Flow == Flow.Break) break;
                if (result.Flow == Flow.Return) return result;
            }
            return default;
        }

        private LuaValue Evaluate(Expression expression, LuaScope scope)
        {
            Enter(expression.Span);
            try
            {
                Tick(expression.Span);
                return expression switch
                {
                    Literal literal => literal.Value,
                    Variable variable => Resolve(scope, variable).Get(variable.Name),
                    Group group => Evaluate(group.Inner, scope),
                    FunctionExpression function => Close(function, scope),
                    Unary unary => EvaluateUnary(unary, scope),
                    Binary binary => EvaluateBinary(binary, scope),
                    Index index => ReadIndex(index, scope),
                    TableExpression table => EvaluateTable(table, scope),
                    Call call => Invoke(call, scope),
                    _ => throw new InvalidOperationException("Unknown Lua expression.")
                };
            }
            finally { _depth--; }
        }

        private LuaValue EvaluateUnary(Unary unary, LuaScope scope)
        {
            var value = Evaluate(unary.Operand, scope);
            if (unary.Operator == Kind.Not) return LuaValue.FromBoolean(!value.IsTruthy);
            if (unary.Operator == Kind.Minus) return LuaValue.FromNumber(-Number(value, unary.Span));
            if (value.Kind == LuaValueKind.String) return LuaValue.FromNumber(value.AsString().Length);
            if (value.Kind != LuaValueKind.Table) throw Error("Length requires a string or table.", unary.Span);
            var table = value.AsTable();
            var length = 0;
            while (table.Get(LuaValue.FromNumber(length + 1)).Kind != LuaValueKind.Nil)
            {
                Tick(unary.Span);
                length++;
            }
            return LuaValue.FromNumber(length);
        }

        private LuaValue EvaluateBinary(Binary binary, LuaScope scope)
        {
            var left = Evaluate(binary.Left, scope);
            if (binary.Operator == Kind.And) return left.IsTruthy ? Evaluate(binary.Right, scope) : left;
            if (binary.Operator == Kind.Or) return left.IsTruthy ? left : Evaluate(binary.Right, scope);
            var right = Evaluate(binary.Right, scope);
            if (binary.Operator == Kind.EqualEqual) return LuaValue.FromBoolean(left.LuaEquals(right));
            if (binary.Operator == Kind.NotEqual) return LuaValue.FromBoolean(!left.LuaEquals(right));
            if (binary.Operator == Kind.Concat) return LuaValue.FromString(ConcatPart(left, binary.Span) + ConcatPart(right, binary.Span));
            if (binary.Operator is Kind.Less or Kind.LessEqual or Kind.Greater or Kind.GreaterEqual)
            {
                if (left.Kind == LuaValueKind.String && right.Kind == LuaValueKind.String)
                    return LuaValue.FromBoolean(Order(binary.Operator, string.CompareOrdinal(left.AsString(), right.AsString()), 0));
                if (left.Kind != LuaValueKind.Number || right.Kind != LuaValueKind.Number)
                    throw Error("Comparison requires two numbers or two strings.", binary.Span);
                return LuaValue.FromBoolean(Order(binary.Operator, left.AsNumber(), right.AsNumber()));
            }
            var a = Number(left, binary.Span);
            var b = Number(right, binary.Span);
            return LuaValue.FromNumber(binary.Operator switch
            {
                Kind.Plus => a + b, Kind.Minus => a - b, Kind.Star => a * b, Kind.Slash => a / b,
                Kind.FloorDiv => Math.Floor(a / b), Kind.Percent => Modulo(a, b), Kind.Power => Math.Pow(a, b),
                _ => throw new InvalidOperationException("Unknown Lua operator.")
            });
        }

        private static bool Order(Kind op, double a, double b) => op switch
        {
            Kind.Less => a < b, Kind.LessEqual => a <= b, Kind.Greater => a > b, Kind.GreaterEqual => a >= b,
            _ => throw new InvalidOperationException("Unknown Lua comparison.")
        };

        private static double Modulo(double a, double b)
        {
            var remainder = a % b;
            return (remainder < 0 && b > 0) || (remainder > 0 && b < 0) ? remainder + b : remainder;
        }

        private LuaValue Invoke(Call call, LuaScope scope)
        {
            var function = Evaluate(call.Target, scope);
            var arguments = EvaluateList(call.Arguments, scope);
            if (function.Kind != LuaValueKind.Function) throw Error("Attempt to call a non-function value.", call.Target.Span);
            if (_callDepth >= maximumCallDepth) throw Error("Maximum Lua call depth exceeded.", call.Span);
            if (function.AsFunction() is LuaNativeFunction native) return native.Execute(output, arguments);
            var closure = (LuaClosure)function.AsFunction();
            var previousFile = _fileName;
            _fileName = closure.FileName;
            _callDepth++;
            try { return ExecuteBlock(closure.Syntax.Body, Bind(closure.Scope, closure.Syntax.Parameters, arguments, closure.Syntax.Span)).Result.Value; }
            finally { _callDepth--; _fileName = previousFile; }
        }

        private LuaValue Close(FunctionExpression function, LuaScope scope) => LuaValue.FromFunction(new LuaClosure(function, scope, _fileName));

        private LuaScope Bind(LuaScope parent, IReadOnlyList<string> names, LuaValue[] values, SourceSpan span)
        {
            if (names.Count == 0) return parent;
            var scope = new LuaScope(parent);
            for (var i = 0; i < names.Count; i++)
            {
                Tick(span);
                scope.Set(names[i], At(values, i));
            }
            return scope;
        }

        private LuaScope Resolve(LuaScope scope, Variable variable)
        {
            while (scope.Parent is { } parent && !scope.Contains(variable.Name))
            {
                Tick(variable.Span);
                scope = parent;
            }
            return scope;
        }

        private LuaValue ReadIndex(Index index, LuaScope scope)
        {
            var target = Evaluate(index.Target, scope);
            var key = Evaluate(index.Key, scope);
            return RequireTable(target, index.Span).Get(key);
        }

        private LuaValue EvaluateTable(TableExpression expression, LuaScope scope)
        {
            var table = new LuaTable();
            var next = 1;
            foreach (var field in expression.Fields)
            {
                var key = field.Key is null ? LuaValue.FromNumber(next++) : Evaluate(field.Key, scope);
                var value = Evaluate(field.Value, scope);
                Put(table, key, value, field.Span);
            }
            return LuaValue.FromTable(table);
        }

        private Destination Locate(Expression target, LuaScope scope)
        {
            Tick(target.Span);
            if (target is Variable variable) return new Destination(Resolve(scope, variable), variable.Name, default, default, variable.Span);
            var index = (Index)target;
            return new Destination(null, null, Evaluate(index.Target, scope), Evaluate(index.Key, scope), index.Span);
        }

        private void Store(Destination destination, LuaValue value)
        {
            if (destination.Scope is { } scope) scope.Set(destination.Name!, value);
            else Put(RequireTable(destination.Target, destination.Span), destination.Key, value, destination.Span);
        }

        private void Put(LuaTable table, LuaValue key, LuaValue value, SourceSpan span)
        {
            if (key.Kind == LuaValueKind.Nil) throw Error("Table index is nil.", span);
            if (key.Kind == LuaValueKind.Number && double.IsNaN(key.AsNumber())) throw Error("Table index is NaN.", span);
            table.Set(key, value);
        }

        private LuaTable RequireTable(LuaValue value, SourceSpan span) => value.Kind == LuaValueKind.Table
            ? value.AsTable() : throw Error("Attempt to index a non-table value.", span);

        private LuaValue[] EvaluateList(IReadOnlyList<Expression> expressions, LuaScope scope)
        {
            if (expressions.Count == 0) return [];
            var values = new LuaValue[expressions.Count];
            for (var i = 0; i < values.Length; i++) values[i] = Evaluate(expressions[i], scope);
            return values;
        }

        private static LuaValue At(LuaValue[] values, int index) => index < values.Length ? values[index] : LuaValue.Nil;

        private double Number(LuaValue value, SourceSpan span)
        {
            if (value.Kind == LuaValueKind.Number) return value.AsNumber();
            if (value.Kind == LuaValueKind.String && LuaLexer.TryNumber(value.AsString(), out var parsed)) return parsed;
            throw Error("Arithmetic requires numbers.", span);
        }

        private string ConcatPart(LuaValue value, SourceSpan span) => value.Kind is LuaValueKind.String or LuaValueKind.Number
            ? value.ToString() : throw Error("Concatenation requires strings or numbers.", span);

        private void Tick(SourceSpan span)
        {
            if (_remaining <= 0) throw Error("Maximum Lua execution steps exceeded.", span);
            _remaining--;
        }

        private void Enter(SourceSpan span)
        {
            if (_depth >= maximumDepth) throw Error("Maximum Lua evaluation depth exceeded.", span);
            _depth++;
        }

        private LuaRuntimeException Error(string message, SourceSpan span) => new(message, span, _fileName);
    }
}
