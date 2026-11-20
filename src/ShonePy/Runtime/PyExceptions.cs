// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShonePy.Parsing;

namespace ShonePy.Runtime;

internal sealed class PyExceptionClass(string name, PyExceptionClass? parent = null)
{
    public string Name { get; } = name;
    public PyExceptionClass? Parent { get; } = parent;

    public PyValue Create(IReadOnlyList<PyValue> args) => PyValue.From(new PyExceptionInstance(this, new PyTuple(args)));

    public bool IsSubclassOf(PyExceptionClass other)
    {
        for (PyExceptionClass? cls = this; cls is not null; cls = cls.Parent)
            if (cls == other) return true;
        return false;
    }

    public override string ToString() => $"<class '{Name}'>";
}

internal sealed class PyExceptionInstance(PyExceptionClass type, PyTuple args)
{
    public PyExceptionClass Type { get; } = type;
    public PyTuple Args { get; } = args;
    public PyExceptionInstance? Context { get; private set; }
    public PyExceptionInstance? Cause { get; private set; }
    public bool SuppressContext { get; private set; }

    public void ChainContext(PyExceptionInstance context)
    {
        if (ReferenceEquals(this, context)) return;

        // Detach the old link back to this instance before making it the head of the chain.
        for (PyExceptionInstance? previous = context; previous is not null; previous = previous.Context)
        {
            if (!ReferenceEquals(previous.Context, this)) continue;
            previous.Context = null;
            break;
        }
        Context = context;
    }

    public void SetCause(PyExceptionInstance? cause)
    {
        Cause = cause;
        SuppressContext = true;
    }

    public override string ToString() => Args.Count switch
    {
        0 => string.Empty,
        1 => Type.Name == "KeyError" ? Args[0].Repr() : Args[0].ToPythonString(),
        _ => Args.ToString()
    };
    public string Repr() => Type.Name + "(" + string.Join(", ", Args.Select(arg => arg.Repr())) + ")";
}

internal static class PyExceptions
{
    private static readonly Dictionary<string, PyExceptionClass> Classes = BuildClasses();

    public static void RegisterBuiltins(PyScope builtins)
    {
        foreach (var (name, cls) in Classes)
            if (!name.Contains('.')) builtins.Define(name, PyValue.From(cls));
    }

    public static PyExceptionInstance InstanceOf(PythonException error) => error.Instance ??= new(
        Classes.TryGetValue(error.ErrorName, out var cls) ? cls : new(error.ErrorName, Classes["Exception"]),
        new PyTuple(error.Message.Length == 0 ? [] : [PyValue.From(error.Message)]));

    public static PythonException AssertionError(PyTuple args, SyntaxNode location)
    {
        var instance = new PyExceptionInstance(Classes["AssertionError"], args);
        return new PythonException("AssertionError", instance.ToString(), location.Line, location.Column) { Instance = instance };
    }

    public static PythonException StopIteration(PyValue result)
    {
        var instance = new PyExceptionInstance(Classes["StopIteration"], result.Type == PyType.None ? new PyTuple() : new PyTuple(result));
        return new PythonException("StopIteration", instance.ToString()) { Instance = instance };
    }

    public static PythonException KeyError(PyValue key) => new("KeyError", key.Repr())
    {
        Instance = new(Classes["KeyError"], new PyTuple([key]))
    };

    public static bool IsStopAsyncIteration(PythonException error) =>
        InstanceOf(error).Type.IsSubclassOf(Classes["StopAsyncIteration"]);

    public static bool Matches(PyExceptionClass actual, PyValue specification, SyntaxNode location)
    {
        if (specification.Data is PyExceptionClass cls) return actual.IsSubclassOf(cls);
        if (specification.Data is PyTuple tuple)
        {
            bool matches = false;
            foreach (var item in tuple) matches |= actual.IsSubclassOf(RequireClass(item, location));
            return matches;
        }
        throw InvalidSpecification(location);
    }

    private static PyExceptionClass RequireClass(PyValue value, SyntaxNode location) =>
        value.Data as PyExceptionClass ?? throw InvalidSpecification(location);

    private static PythonException InvalidSpecification(SyntaxNode location) =>
        new("TypeError", "catching classes that do not inherit from BaseException is not allowed", location.Line, location.Column);

    private static Dictionary<string, PyExceptionClass> BuildClasses()
    {
        var classes = new Dictionary<string, PyExceptionClass>(StringComparer.Ordinal)
        {
            ["BaseException"] = new("BaseException")
        };
        Add("BaseException", "Exception", "SystemExit", "KeyboardInterrupt", "GeneratorExit");
        Add("Exception", "ArithmeticError", "AttributeError", "ImportError", "LookupError", "NameError", "RuntimeError",
            "SyntaxError", "TypeError", "ValueError", "StopIteration", "StopAsyncIteration", "OSError", "AssertionError", "re.error");
        Add("ArithmeticError", "ZeroDivisionError", "OverflowError", "FloatingPointError");
        Add("ImportError", "ModuleNotFoundError");
        Add("LookupError", "IndexError", "KeyError");
        Add("NameError", "UnboundLocalError");
        Add("RuntimeError", "NotImplementedError", "RecursionError");
        Add("ValueError", "JSONDecodeError", "UnicodeError");
        Add("UnicodeError", "UnicodeEncodeError");
        Add("SyntaxError", "IndentationError");
        Add("IndentationError", "TabError");
        Add("OSError", "TimeoutError", "FileNotFoundError", "PermissionError");
        return classes;

        void Add(string parent, params string[] names)
        {
            foreach (string name in names) classes.Add(name, new(name, classes[parent]));
        }
    }
}
