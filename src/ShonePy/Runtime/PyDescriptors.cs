// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Runtime;

internal abstract class PyDescriptor
{
    internal abstract string TypeName { get; }
    internal abstract PyValue Bind(PyInstance? instance, PyClass owner);
    internal abstract PyValue GetAttribute(string name);

    protected PythonException MissingAttribute(string name) =>
        new("AttributeError", $"'{TypeName}' object has no attribute '{name}'");
}

internal sealed class PyMethodDescriptor(PyValue function, bool bindsClass) : PyDescriptor
{
    internal override string TypeName => bindsClass ? "classmethod" : "staticmethod";
    internal bool BindsClass => bindsClass;
    internal PyValue Function => function;

    internal static PyCallable Constructor(bool bindsClass)
    {
        string name = bindsClass ? "classmethod" : "staticmethod";
        return new PyCallable(args => args.Count == 1
            ? PyValue.From(new PyMethodDescriptor(args[0], bindsClass))
            : throw new PythonException("TypeError", $"{name} expected 1 argument, got {args.Count}"), name);
    }

    internal override PyValue Bind(PyInstance? instance, PyClass owner) =>
        bindsClass ? BindMethod(function, PyValue.From(owner)) : function;

    internal static PyValue BindMethod(PyValue function, PyValue receiver) =>
        PyValue.From(new PyCallable(args => function.Call([receiver, .. args]), (function.Data as PyCallable)?.Name)
        {
            InvokeWithKeywords = (args, keywords) => function.Call([receiver, .. args], keywords),
            Attributes = new PyDict { ["__self__"] = receiver, ["__func__"] = function }
        });

    internal override PyValue GetAttribute(string name) => name switch
    {
        "__func__" or "__wrapped__" => function,
        _ => throw MissingAttribute(name)
    };
}

internal sealed class PyProperty(PyValue getter, PyValue setter, PyValue deleter, PyValue doc) : PyDescriptor
{
    private static readonly string[] ParameterNames = ["fget", "fset", "fdel", "doc"];
    internal override string TypeName => "property";
    private PyValue Doc => doc.Type == PyType.None && getter.Data is PyCallable function ? function.Doc : doc;

    internal static PyCallable Constructor { get; } = new(args => Create(args, null), "property")
    {
        InvokeWithKeywords = Create
    };

    private static PyValue Create(IReadOnlyList<PyValue> args, IReadOnlyDictionary<string, PyValue>? keywords)
    {
        if (args.Count > ParameterNames.Length)
            throw new PythonException("TypeError", $"property() takes at most 4 arguments ({args.Count} given)");

        var values = new PyValue[ParameterNames.Length];
        for (int i = 0; i < args.Count; i++) values[i] = args[i];
        if (keywords is not null)
        {
            foreach (var (name, value) in keywords)
            {
                int index = Array.IndexOf(ParameterNames, name);
                if (index < 0)
                    throw new PythonException("TypeError", $"'{name}' is an invalid keyword argument for property()");
                if (index < args.Count)
                    throw new PythonException("TypeError", $"argument for property() given by name ('{name}') and position ({index + 1})");
                values[index] = value;
            }
        }
        return PyValue.From(new PyProperty(values[0], values[1], values[2], values[3]));
    }

    internal override PyValue Bind(PyInstance? instance, PyClass owner) => instance is null
        ? PyValue.From(this)
        : Read(PyValue.From(instance), owner);

    private PyValue Read(PyValue instance, PyClass? owner)
    {
        if (getter.Type == PyType.None) throw MissingAccessor("getter", owner);
        return getter.Call([instance]);
    }

    internal void Set(PyInstance instance, PyValue value) => Write(PyValue.From(instance), value, instance.Class);

    private void Write(PyValue instance, PyValue value, PyClass? owner)
    {
        if (setter.Type == PyType.None) throw MissingAccessor("setter", owner);
        setter.Call([instance, value]);
    }

    internal void Delete(PyInstance instance) => Delete(PyValue.From(instance), instance.Class);

    private void Delete(PyValue instance, PyClass? owner)
    {
        if (deleter.Type == PyType.None) throw MissingAccessor("deleter", owner);
        deleter.Call([instance]);
    }

    private static PythonException MissingAccessor(string accessor, PyClass? owner) =>
        new("AttributeError", $"property of '{owner?.Name ?? "object"}' object has no {accessor}");

    internal override PyValue GetAttribute(string name) => name switch
    {
        "fget" => getter,
        "fset" => setter,
        "fdel" => deleter,
        "__doc__" => Doc,
        "getter" => Method(name, 1, args => PyValue.From(new PyProperty(Replace(getter, args[0]), setter, deleter, doc))),
        "setter" => Method(name, 1, args => PyValue.From(new PyProperty(getter, Replace(setter, args[0]), deleter, doc))),
        "deleter" => Method(name, 1, args => PyValue.From(new PyProperty(getter, setter, Replace(deleter, args[0]), doc))),
        "__get__" => PyValue.From(new PyCallable(Get, name)),
        "__set__" => Method(name, 2, args => { Write(args[0], args[1], OwnerOf(args[0])); return PyValue.None; }),
        "__delete__" => Method(name, 1, args => { Delete(args[0], OwnerOf(args[0])); return PyValue.None; }),
        _ => throw MissingAttribute(name)
    };

    private PyValue Get(IReadOnlyList<PyValue> args)
    {
        if (args.Count is < 1 or > 2)
            throw new PythonException("TypeError", "property.__get__() expected one or two arguments");
        if (args[0].Type != PyType.None) return Read(args[0], OwnerOf(args[0]));
        if (args.Count == 2 && args[1].Type != PyType.None) return PyValue.From(this);
        throw new PythonException("TypeError", "__get__(None, None) is invalid");
    }

    private static PyValue Replace(PyValue oldValue, PyValue newValue) => newValue.Type == PyType.None ? oldValue : newValue;

    private static PyClass? OwnerOf(PyValue instance) => (instance.Data as PyInstance)?.Class;

    private static PyValue Method(string name, int arity, Func<IReadOnlyList<PyValue>, PyValue> invoke) =>
        PyValue.From(new PyCallable(args => args.Count == arity ? invoke(args)
            : throw new PythonException("TypeError", $"property.{name}() expected {arity} argument(s), got {args.Count}"), name));
}
