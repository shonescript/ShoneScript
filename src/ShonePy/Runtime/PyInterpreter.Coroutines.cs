// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShonePy.Parsing;

namespace ShonePy.Runtime;

public sealed partial class PyInterpreter
{
    public PyValue RunCoroutine(PyValue coroutine) => RunWithBudget(() => AwaitCoroutine(coroutine));

    private enum AwaitContext { Expression, AsyncFor, AsyncNextDefault, AsyncWithEnter, AsyncWithExit }

    private PyValue AwaitCoroutine(PyValue value, AwaitContext context = AwaitContext.Expression)
    {
        if (value.Data is PyCoroutine coroutine)
        {
            if (!ReferenceEquals(coroutine.Owner, this))
                throw Error("TypeError", "coroutine belongs to another Python engine");
            Tick();
            return coroutine.Run();
        }

        if (value.Data is PyInstance instance && instance.Class.TryLookup("__await__", out _))
            throw Error("NotImplementedError", "custom awaitables are not supported");
        var type = PyOperations.TypeName(value);
        throw Error("TypeError", context switch
        {
            AwaitContext.AsyncFor => $"'async for' received an invalid object from __anext__: {type}",
            AwaitContext.AsyncNextDefault => $"'{type}' object can't be awaited",
            AwaitContext.AsyncWithEnter => $"'async with' received an object from __aenter__ that does not implement __await__: {type}",
            AwaitContext.AsyncWithExit => $"'async with' received an object from __aexit__ that does not implement __await__: {type}",
            _ => $"object {type} can't be used in 'await' expression"
        });
    }

    private PyValue AsyncIteratorBuiltin(IReadOnlyList<PyValue> args)
    {
        if (args.Count != 1)
            throw Error("TypeError", $"aiter() takes exactly one argument ({args.Count} given)");
        return GetAsyncIterator(args[0], asyncFor: false);
    }

    private PyValue AsyncNextBuiltin(IReadOnlyList<PyValue> args)
    {
        if (args.Count == 0) throw Error("TypeError", "anext expected at least 1 argument, got 0");
        if (args.Count > 2) throw Error("TypeError", $"anext expected at most 2 arguments, got {args.Count}");

        // __anext__ is called immediately, even when a default is supplied.
        var awaitable = GetAsyncNext(args[0]);
        if (args.Count == 1) return awaitable;
        var fallback = args[1];
        return PyValue.From(new PyCoroutine(this, "anext", () =>
        {
            try { return AwaitCoroutine(awaitable, AwaitContext.AsyncNextDefault); }
            catch (PythonException error) when (PyExceptions.IsStopAsyncIteration(error)) { return fallback; }
        }));
    }

    private PyValue GetAsyncIterator(PyValue iterable, bool asyncFor)
    {
        if (iterable.Data is not PyInstance instance || !instance.TryGetSpecial("__aiter__", out var aiter))
        {
            var type = PyOperations.TypeName(iterable);
            throw Error("TypeError", asyncFor ? $"'async for' requires an object with __aiter__ method, got {type}"
                : $"'{type}' object is not an async iterable");
        }

        var iterator = aiter.Call([]);
        // Check for the slot without binding it. A descriptor must run only on retrieval.
        if (iterator.Data is not PyInstance result || !result.Class.TryLookup("__anext__", out _))
        {
            var type = PyOperations.TypeName(iterator);
            throw Error("TypeError", asyncFor
                ? $"'async for' received an object from __aiter__ that does not implement __anext__: {type}"
                : $"{instance.Class.Name}.__aiter__() must return an async iterator, not {type}");
        }
        return iterator;
    }

    private PyValue GetAsyncNext(PyValue iterator)
    {
        if (iterator.Data is not PyInstance instance || !instance.TryGetSpecial("__anext__", out var anext))
            throw Error("TypeError", $"'{PyOperations.TypeName(iterator)}' object is not an async iterator");
        return anext.Call([]);
    }

    private IEnumerable<PyValue> IterateAsync(PyValue iterable, ForStatement statement) =>
        ReadAsyncIterator(GetAsyncIterator(iterable, asyncFor: true), statement);

    private IEnumerable<PyValue> ReadAsyncIterator(PyValue iterator, SyntaxNode location)
    {
        while (true)
        {
            Tick(location);
            PyValue item;
            try { item = AwaitCoroutine(GetAsyncNext(iterator), AwaitContext.AsyncFor); }
            catch (PythonException error) when (PyExceptions.IsStopAsyncIteration(error)) { yield break; }
            yield return item;
        }
    }

    private PyValue ExecuteCoroutineBody(FunctionDefinition function, PyScope locals)
    {
        // Managed coroutines only await other managed coroutines, so execution can
        // reuse the regular evaluator without a resumable generator frame.
        var previousModule = _currentModule;
        var previousException = _activeException;
        _currentModule = null;
        try { return ExecuteFunctionBody(function, locals); }
        catch (BudgetSignal limit)
        {
            limit.Error = AddFileName(limit.Error, function.FileName);
            throw;
        }
        catch (PythonException error) when (error.ErrorName == "StopIteration")
        {
            var wrapped = new PythonException("RuntimeError", "coroutine raised StopIteration",
                error.Line, error.Column, error.FileName ?? function.FileName) { ContextInitialized = true };
            var original = PyExceptions.InstanceOf(error);
            var instance = PyExceptions.InstanceOf(wrapped);
            instance.ChainContext(original);
            instance.SetCause(original);
            throw AddLocation(wrapped, function);
        }
        catch (PythonException error) when (error.FileName is null && function.FileName is not null)
        {
            throw AddFileName(error, function.FileName);
        }
        finally
        {
            _activeException = previousException;
            _currentModule = previousModule;
        }
    }
}

internal sealed class PyCoroutine(PyInterpreter owner, string name, Func<PyValue> execute)
{
    private Func<PyValue>? _execute = execute;
    private bool _running;

    internal PyInterpreter Owner { get; } = owner;

    internal PyValue Run()
    {
        if (_running) throw new PythonException("ValueError", "coroutine already executing");
        if (_execute is not { } body) throw new PythonException("RuntimeError", "cannot reuse already awaited coroutine");
        _running = true;
        try { return body(); }
        finally
        {
            _running = false;
            _execute = null;
        }
    }

    public override string ToString() => $"<coroutine object {name}>";
}
