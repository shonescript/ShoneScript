// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using ShoneJs.Parsing;

namespace ShoneJs.Runtime;

// A single-consumer awaitable with inline continuations. Only a JS Promise reaction can
// suspend these operations; no CLR scheduler or SynchronizationContext owns engine work.
[AsyncMethodBuilder(typeof(JsOperationBuilder<>))]
internal sealed class JsOperation<T> : ICriticalNotifyCompletion
{
    private T _result = default!;
    private ExceptionDispatchInfo? _error;
    private Action? _continuation;
#if DEBUG
    private bool _hasContinuation;
#endif
    public bool IsCompleted { get; private set; }
    public JsOperation<T> GetAwaiter() => this;
    public T GetResult() { _error?.Throw(); return _result; }
    public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);
    public void UnsafeOnCompleted(Action continuation)
    {
#if DEBUG
        Debug.Assert(!_hasContinuation, "A JsOperation supports only one continuation.");
        _hasContinuation = true;
#endif
        if (IsCompleted) continuation();
        else _continuation = continuation;
    }
    public void Complete(T result, Exception? error = null)
    {
        _result = result;
        if (error is not null) _error = ExceptionDispatchInfo.Capture(error);
        IsCompleted = true;
        var continuation = _continuation;
        _continuation = null;
        continuation?.Invoke();
    }
}

internal struct JsOperationBuilder<T>
{
    public static JsOperationBuilder<T> Create() => new() { Task = new JsOperation<T>() };
    public JsOperation<T> Task { get; private set; }
    public void SetResult(T result) => Task.Complete(result);
    public void SetException(Exception error) => Task.Complete(default!, error);
    public void SetStateMachine(IAsyncStateMachine stateMachine) { }
    public void Start<TState>(ref TState state) where TState : IAsyncStateMachine => state.MoveNext();
    public void AwaitOnCompleted<TAwaiter, TState>(ref TAwaiter awaiter, ref TState state)
        where TAwaiter : INotifyCompletion where TState : IAsyncStateMachine => awaiter.OnCompleted(state.MoveNext);
    public void AwaitUnsafeOnCompleted<TAwaiter, TState>(ref TAwaiter awaiter, ref TState state)
        where TAwaiter : ICriticalNotifyCompletion where TState : IAsyncStateMachine => awaiter.UnsafeOnCompleted(state.MoveNext);
}

public sealed partial class Interpreter
{
    private readonly record struct ExecutionContext(EnvironmentRecord Env, EnvironmentRecord VarEnv, bool Strict, JsValue This, ModuleRecord? Module);
    private ExecutionContext CaptureContext() => new(_env, _varEnv, _strict, _this, _activeModule);
    private void RestoreContext(ExecutionContext context) => (_env, _varEnv, _strict, _this, _activeModule) =
        (context.Env, context.VarEnv, context.Strict, context.This, context.Module);

    private JsValue StartAsync(Func<JsOperation<JsValue>> start)
    {
        var promise = _promises.Create(out var resolve, out var reject);
        CompleteAsync(start(), resolve, reject);
        return promise;
    }

    private void CompleteAsync(JsOperation<JsValue> operation, Action<JsValue> resolve, Action<JsValue> reject)
    {
        operation.UnsafeOnCompleted(() =>
        {
            try { resolve(operation.GetResult()); }
            catch (JsException error) { AttachErrorPrototype(error); reject(error.Value); }
        });
    }

    private JsOperation<JsValue> Suspend(JsValue value, bool awaitEvenIfSettled = true)
    {
        var result = new JsOperation<JsValue>();
        if (!awaitEvenIfSettled && _promises.TryGetSettled(value, out var settled, out var rejected))
        {
            result.Complete(settled, rejected ? new JsException(settled) : null);
            return result;
        }
        var suspended = CaptureContext();
        var token = _operationCancellation;
        void Resume(JsValue response, bool rejected)
        {
            var previous = CaptureContext();
            RestoreContext(suspended);
            try
            {
                token.ThrowIfCancellationRequested();
                ConsumeStep();
                result.Complete(response, rejected ? new JsException(response) : null);
            }
            finally { RestoreContext(previous); }
        }
        _promises.Observe(value, response => Resume(response, false), response => Resume(response, true));
        return result;
    }

    private async JsOperation<JsValue> InvokeAsyncFunction(List<string> parameters, JsValue[] args, BlockStmt? body, Expr? expression)
    {
        BindParameters(parameters, args);
        if (expression is not null) return await EvalAsync(expression);
        PrepareScope(body!.Body, _env, functionScope: true, parameterScope: _varEnv);
        return FunctionCompletionValue(await ExecListAsync(body.Body));
    }

    private async JsOperation<Completion> ExecListAsync(IEnumerable<Stmt> statements)
    {
        var result = Completion.Normal(JsValue.Undefined);
        foreach (var statement in statements)
        {
            var completion = await ExecAsync(statement);
            if (completion.IsAbrupt) return completion;
            if (ProducesListValue(statement)) result = completion;
        }
        return result;
    }

    private async JsOperation<Completion> ExecAsync(Stmt statement)
    {
        ConsumeStep();
        switch (statement)
        {
            case BlockStmt block:
            {
                var previous = _env;
                _env = new EnvironmentRecord(previous);
                try { PrepareScope(block.Body, _env); return await ExecListAsync(block.Body); }
                finally { _env = previous; }
            }
            case VarStmt variable:
                return InitializeVariable(variable, variable.Init is null ? JsValue.Undefined : await EvalAsync(variable.Init));
            case VarListStmt list: return await ExecListAsync(list.Declarations);
            case ExprStmt expression: return Completion.Normal(await EvalAsync(expression.Expression));
            case IfStmt branch:
                return (await EvalAsync(branch.Test)).ToBoolean() ? await ExecAsync(branch.Then)
                    : branch.Else is null ? Completion.Normal(JsValue.Undefined) : await ExecAsync(branch.Else);
            case ReturnStmt returned:
                return Completion.Return(returned.Value is null ? JsValue.Undefined : await EvalAsync(returned.Value));
            case ThrowStmt thrown: throw new JsException(await EvalAsync(thrown.Value));
            case WhileStmt loop: return await LoopAsync(loop.Test, loop.Body, false);
            case DoWhileStmt loop: return await LoopAsync(loop.Test, loop.Body, true);
            case ForStmt loop: return await ForAsync(loop);
            case SwitchStmt selection: return await SwitchAsync(selection);
            case TryStmt attempt: return await TryAsync(attempt);
            default: return ExecCore(statement);
        }
    }

    private async JsOperation<Completion> LoopAsync(Expr test, Stmt body, bool runFirst)
    {
        var result = Completion.Normal(JsValue.Undefined);
        while (runFirst || (await EvalAsync(test)).ToBoolean())
        {
            runFirst = false;
            ConsumeIteration();
            var completion = await ExecAsync(body);
            if (completion.Kind == CompletionKind.Break) break;
            if (completion.Kind == CompletionKind.Return) return completion;
            if (!completion.IsAbrupt) result = completion;
        }
        return result;
    }

    private async JsOperation<Completion> ForAsync(ForStmt loop)
    {
        var previous = _env;
        try
        {
            var lexical = PrepareForScope(loop, previous);
            if (loop.Init is not null)
            {
                var initial = await ExecAsync(loop.Init);
                if (initial.IsAbrupt) return initial;
            }
            var result = Completion.Normal(JsValue.Undefined);
            while (loop.Test is null || (await EvalAsync(loop.Test)).ToBoolean())
            {
                ConsumeIteration();
                var completion = await ExecAsync(loop.Body);
                if (completion.Kind == CompletionKind.Break) break;
                if (completion.Kind == CompletionKind.Return) return completion;
                if (!completion.IsAbrupt) result = completion;
                AdvanceForScope(lexical, previous);
                if (loop.Update is not null) await EvalAsync(loop.Update);
            }
            return result;
        }
        finally { _env = previous; }
    }

    private async JsOperation<Completion> SwitchAsync(SwitchStmt selection)
    {
        var value = await EvalAsync(selection.Discriminant);
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
                else if (AbstractOperations.StrictEqual(await EvalAsync(test), value)) { match = i; break; }
            }
            if (match < 0) match = fallback;
            var result = Completion.Normal(JsValue.Undefined);
            if (match < 0) return result;
            for (var i = match; i < selection.Cases.Count; i++)
            {
                var completion = await ExecListAsync(selection.Cases[i].Body);
                if (completion.Kind == CompletionKind.Break) break;
                if (completion.IsAbrupt) return completion;
                result = completion;
            }
            return result;
        }
        finally { _env = previous; }
    }

    private async JsOperation<Completion> TryAsync(TryStmt attempt)
    {
        var result = default(Completion);
        ExceptionDispatchInfo? pendingThrow = null;
        try
        {
            try { result = await ExecAsync(attempt.Body); }
            catch (JsException error) when (attempt.CatchBody is not null)
            {
                var previous = _env;
                try
                {
                    EnterCatchScope(attempt, error);
                    result = await ExecAsync(attempt.CatchBody);
                }
                finally { _env = previous; }
            }
        }
        catch (JsException error) when (attempt.FinallyBody is not null)
        {
            pendingThrow = ExceptionDispatchInfo.Capture(error);
        }

        return attempt.FinallyBody is null ? result
            : FinishTry(result, pendingThrow, await ExecAsync(attempt.FinallyBody));
    }

    private async JsOperation<JsValue> EvalAsync(Expr expression)
    {
        ConsumeStep();
        switch (expression)
        {
            case AwaitExpr awaitExpr: return await Suspend(await EvalAsync(awaitExpr.Operand));
            case MemberExpr or OptionalChainExpr: return (await ResolveAsync(expression)).Read();
            case AssignExpr assignment:
            {
                var reference = await ResolveAsync(assignment.Target);
                var value = await EvalAsync(assignment.Value);
                reference.Write(value);
                return value;
            }
            case CompoundAssignExpr assignment:
            {
                var reference = await ResolveAsync(assignment.Target);
                var prior = reference.Read();
                if (assignment.Op == TokenKind.AndAnd && !prior.ToBoolean() || assignment.Op == TokenKind.OrOr && prior.ToBoolean() ||
                    assignment.Op == TokenKind.NullishCoalescing && !AbstractOperations.IsNullish(prior)) return prior;
                var value = await EvalAsync(assignment.Value);
                if (assignment.Op is not (TokenKind.AndAnd or TokenKind.OrOr or TokenKind.NullishCoalescing)) value = ApplyBinary(assignment.Op, prior, value);
                reference.Write(value);
                return value;
            }
            case UpdateExpr update: return Update(update, await ResolveAsync(update.Target));
            case UnaryExpr unary:
                if (unary.Op == TokenKind.Typeof && UnwrapTypeExpression(unary.Value) is IdentifierExpr) return Unary(unary);
                if (unary.Op == TokenKind.Delete && IsReference(unary.Value))
                {
                    var deleted = (await ResolveAsync(unary.Value)).Delete();
                    if (!deleted && _strict) throw JsException.TypeError("Cannot delete property");
                    return JsValue.From(deleted);
                }
                return Unary(unary with { Value = new LiteralExpr(await EvalAsync(unary.Value)) });
            case BinaryExpr binary:
            {
                var left = await EvalAsync(binary.Left);
                if (binary.Op == TokenKind.AndAnd && !left.ToBoolean() || binary.Op == TokenKind.OrOr && left.ToBoolean() ||
                    binary.Op == TokenKind.NullishCoalescing && !AbstractOperations.IsNullish(left)) return left;
                var right = await EvalAsync(binary.Right);
                return binary.Op is TokenKind.AndAnd or TokenKind.OrOr or TokenKind.NullishCoalescing ? right : ApplyBinary(binary.Op, left, right);
            }
            case CallExpr call:
            {
                var reference = IsReference(call.Target) ? await ResolveAsync(call.Target) : ValueReference(await EvalAsync(call.Target));
                return await InvokeAsync(reference.Read(), reference.Receiver, call.Arguments);
            }
            case NewExpr construct:
            {
                var target = await EvalAsync(construct.Target);
                var arguments = await ArgumentsAsync(construct.Arguments);
                if (target.Data is not JsFunction function) throw JsException.TypeError("Value is not a constructor");
                return function.Construct(arguments);
            }
            case ConditionalExpr conditional:
                return await EvalAsync((await EvalAsync(conditional.Test)).ToBoolean() ? conditional.Then : conditional.Else);
            case SequenceExpr sequence:
            {
                var value = JsValue.Undefined;
                foreach (var item in sequence.Expressions) value = await EvalAsync(item);
                return value;
            }
            case ArrayExpr array:
            {
                var obj = new JsArray { Prototype = _arrayPrototype };
                foreach (var item in array.Items)
                {
                    ConsumeStep();
                    if (item is ArrayHoleExpr) obj.Set("length", JsValue.From(obj.ArrayLength + 1d));
                    else obj.Add(await EvalAsync(item));
                }
                return JsValue.From(obj);
            }
            case ObjectExpr obj:
            {
                var result = new JsObject { Prototype = _objectPrototype };
                foreach (var property in obj.Properties)
                {
                    var key = Builtins.ObjectBuiltins.ToPropertyKey(await EvalAsync(property.Key));
                    DefineLiteralProperty(result, key, await EvalAsync(property.Value), property.Kind);
                }
                return JsValue.From(result);
            }
            case TemplateExpr template:
            {
                var text = new StringBuilder();
                for (var i = 0; i < template.Quasis.Count; i++)
                {
                    text.Append(template.Quasis[i].Cooked);
                    if (i < template.Expressions.Count)
                        text.Append(AbstractOperations.ToPrimitive(await EvalAsync(template.Expressions[i]), preferString: true).ToJsString());
                }
                return JsValue.From(text.ToString());
            }
            case TypeAssertionExpr assertion: return await EvalAsync(assertion.Expression);
            case SatisfiesExpr satisfies: return await EvalAsync(satisfies.Expression);
            case NonNullExpr assertion: return await EvalAsync(assertion.Expression);
            default: return EvalCore(expression);
        }
    }

    private async JsOperation<Reference> ResolveAsync(Expr expression)
    {
        ConsumeStep();
        expression = UnwrapTypeExpression(expression);
        if (expression is MemberExpr member) return MemberReference(await EvalAsync(member.Target), await EvalAsync(member.Key));
        if (expression is not OptionalChainExpr chain) return Resolve(expression);
        var current = IsReference(chain.Target) ? await ResolveAsync(chain.Target) : ValueReference(await EvalAsync(chain.Target));
        foreach (var segment in chain.Segments)
        {
            var value = current.Read();
            if (segment.Optional && AbstractOperations.IsNullish(value)) return ValueReference(JsValue.Undefined);
            current = segment switch
            {
                ChainMember property => MemberReference(value, await EvalAsync(property.Key)),
                ChainCall call => ValueReference(await InvokeAsync(value, current.Receiver, call.Arguments)),
                _ => throw new NotSupportedException()
            };
        }
        return current;
    }

    private async JsOperation<JsValue[]> ArgumentsAsync(List<Expr> arguments)
    {
        var values = new JsValue[arguments.Count];
        for (var i = 0; i < arguments.Count; i++) values[i] = await EvalAsync(arguments[i]);
        return values;
    }

    private async JsOperation<JsValue> InvokeAsync(JsValue value, JsValue receiver, List<Expr> args)
    {
        var arguments = await ArgumentsAsync(args);
        if (value.Data is not JsFunction function) throw JsException.TypeError("Value is not callable");
        return function.Call(receiver, arguments);
    }
}
