// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShonePy.Parsing;

namespace ShonePy.Runtime;

public sealed partial class PyInterpreter
{
    private sealed class GeneratorFrame
    {
        // Slots hold results independently of values emitted to the caller.
        internal PyValue Value;
    }

    private PyValue CreateGenerator(FunctionDefinition function, PyScope locals)
    {
        var frame = new GeneratorFrame();
        return CreateGenerator(function, locals, RunGeneratorBlock(function.Body, frame).GetEnumerator());
    }

    private PyValue CreateGenerator(SyntaxNode definition, PyScope locals, IEnumerator<PyValue> cursor)
    {
        return PyValue.From(PyIterator.ForGenerator(() =>
        {
            var previousScope = _scope;
            var previousModule = _currentModule;
            var previousException = _activeException;
            bool entered = false;
            try
            {
                CheckCallDepth(definition);
                _callDepth++;
                entered = true;
                _scope = locals;
                // Function locals must not be copied into the caller's module namespace.
                _currentModule = null;
                if (cursor.MoveNext()) return (cursor.Current, PyValue.None);
                cursor.Dispose();
                return (null, PyValue.None);
            }
            catch (ReturnSignal result)
            {
                cursor.Dispose();
                return (null, result.Value);
            }
            catch (PythonException error) when (error.ErrorName == "StopIteration")
            {
                cursor.Dispose();
                var wrapped = new PythonException("RuntimeError", "generator raised StopIteration",
                    error.Line, error.Column, error.FileName);
                var original = PyExceptions.InstanceOf(error);
                var instance = PyExceptions.InstanceOf(wrapped);
                instance.ChainContext(original);
                instance.SetCause(original);
                throw AddLocation(wrapped, definition);
            }
            catch
            {
                cursor.Dispose();
                throw;
            }
            finally
            {
                if (entered)
                {
                    _callDepth--;
                    _scope = previousScope;
                    _currentModule = previousModule;
                    _activeException = previousException;
                }
            }
        }));
    }

    private IEnumerable<PyValue> RunGeneratorBlock(IEnumerable<StatementSyntax> statements, GeneratorFrame frame)
    {
        foreach (var statement in statements)
        {
            Tick(statement);
            using var execution = RunGeneratorStatement(statement, frame).GetEnumerator();
            while (true)
            {
                bool hasValue;
                try { hasValue = execution.MoveNext(); }
                catch (PythonException error) { throw AddLocation(ChainExceptionContext(error), statement); }
                catch (BudgetSignal limit) { limit.Error = AddLocation(limit.Error, statement); throw; }
                catch (Exception error) when (error is not (ReturnSignal or BreakSignal or ContinueSignal))
                { throw ChainExceptionContext(Error("RuntimeError", error.Message, statement)); }
                if (!hasValue) break;
                yield return execution.Current;
            }
        }
    }

    private IEnumerable<PyValue> RunGeneratorStatement(StatementSyntax statement, GeneratorFrame frame)
    {
        switch (statement)
        {
            case ExpressionStatement expression:
                foreach (var value in RunGeneratorExpression(expression.Expression, frame)) yield return value;
                break;
            case AssignmentStatement assignment:
                foreach (var value in RunGeneratorExpression(assignment.Value, frame)) yield return value;
                foreach (var target in assignment.Targets) AssignTarget(target, frame.Value);
                break;
            case ReturnStatement result:
                frame.Value = PyValue.None;
                if (result.Value is { } returned)
                    foreach (var value in RunGeneratorExpression(returned, frame)) yield return value;
                throw new ReturnSignal(frame.Value);
            case IfStatement branch:
                foreach (var value in RunGeneratorExpression(branch.Test, frame)) yield return value;
                foreach (var value in RunGeneratorBlock(frame.Value.IsTruthy() ? branch.Body : branch.ElseBody, frame))
                    yield return value;
                break;
            case WhileStatement loop:
                while (true)
                {
                    foreach (var value in RunGeneratorExpression(loop.Test, frame)) yield return value;
                    if (!frame.Value.IsTruthy()) break;
                    Tick(loop);
                    var control = new LoopControl();
                    foreach (var value in RunGeneratorLoopBody(loop.Body, frame, control)) yield return value;
                    if (control.Break) yield break;
                }
                foreach (var value in RunGeneratorBlock(loop.ElseBody, frame)) yield return value;
                break;
            case ForStatement loop:
                foreach (var value in RunGeneratorExpression(loop.Iterable, frame)) yield return value;
                foreach (var item in PyOperations.Iterate(frame.Value))
                {
                    Tick(loop);
                    AssignTarget(loop.Target, item);
                    var control = new LoopControl();
                    foreach (var value in RunGeneratorLoopBody(loop.Body, frame, control)) yield return value;
                    if (control.Break) yield break;
                }
                foreach (var value in RunGeneratorBlock(loop.ElseBody, frame)) yield return value;
                break;
            default:
                // Other constructs execute normally until an unsupported yield position is reached.
                ExecuteStatement(statement);
                break;
        }
    }

    private sealed class LoopControl { internal bool Break; }

    private IEnumerable<PyValue> RunGeneratorLoopBody(List<StatementSyntax> body, GeneratorFrame frame, LoopControl control)
    {
        using var execution = RunGeneratorBlock(body, frame).GetEnumerator();
        while (true)
        {
            bool hasValue;
            try { hasValue = execution.MoveNext(); }
            catch (BreakSignal) { control.Break = true; yield break; }
            catch (ContinueSignal) { yield break; }
            if (!hasValue) yield break;
            yield return execution.Current;
        }
    }

    private IEnumerable<PyValue> RunGeneratorExpression(ExpressionSyntax expression, GeneratorFrame frame)
    {
        // Ordinary expression subtrees use the same evaluator as non-generator code.
        if (!HasResumableExpression(expression))
        {
            frame.Value = Evaluate(expression);
            yield break;
        }

        Tick(expression);
        switch (expression)
        {
            case YieldExpression suspension:
                if (suspension.Value is not null)
                    foreach (var value in RunGeneratorExpression(suspension.Value, frame)) yield return value;
                else frame.Value = PyValue.None;

                if (suspension.IsDelegating)
                {
                    var iterator = PyOperations.GetIterator(frame.Value);
                    PyValue result;
                    while (iterator.Next(out result) is { } next) yield return next;
                    frame.Value = result;
                }
                else
                {
                    yield return frame.Value;
                    frame.Value = PyValue.None;
                }
                break;
            case UnaryExpression unary:
                foreach (var value in RunGeneratorExpression(unary.Operand, frame)) yield return value;
                frame.Value = PyOperations.Unary(unary.Operator, frame.Value);
                break;
            case BinaryExpression binary:
                foreach (var value in RunGeneratorExpression(binary.Left, frame)) yield return value;
                var left = frame.Value;
                if (binary.Operator == "and" && !left.IsTruthy() || binary.Operator == "or" && left.IsTruthy()) break;
                foreach (var value in RunGeneratorExpression(binary.Right, frame)) yield return value;
                frame.Value = ApplyBinary(left, binary.Operator, frame.Value);
                break;
            case ComparisonChainExpression comparison:
                foreach (var value in RunGeneratorExpression(comparison.Operands[0], frame)) yield return value;
                var previous = frame.Value;
                for (int i = 0; i < comparison.Operators.Count; i++)
                {
                    foreach (var value in RunGeneratorExpression(comparison.Operands[i + 1], frame)) yield return value;
                    if (!Compare(previous, comparison.Operators[i], frame.Value))
                    {
                        frame.Value = PyValue.False;
                        yield break;
                    }
                    previous = frame.Value;
                }
                frame.Value = PyValue.True;
                break;
            case ConditionalExpression choice:
                foreach (var value in RunGeneratorExpression(choice.Test, frame)) yield return value;
                foreach (var value in RunGeneratorExpression(frame.Value.IsTruthy() ? choice.WhenTrue : choice.WhenFalse, frame)) yield return value;
                break;
        }
    }

    private static bool HasResumableExpression(ExpressionSyntax expression) => expression switch
    {
        YieldExpression => true,
        UnaryExpression unary => HasResumableExpression(unary.Operand),
        BinaryExpression binary => HasResumableExpression(binary.Left) || HasResumableExpression(binary.Right),
        ComparisonChainExpression comparison => comparison.Operands.Any(HasResumableExpression),
        ConditionalExpression choice => HasResumableExpression(choice.Test) || HasResumableExpression(choice.WhenTrue) || HasResumableExpression(choice.WhenFalse),
        _ => false
    };
}
