// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using Sdcb.Arithmetic;
using ShonePy.Modules;
using ShonePy.Runtime;

namespace ShonePy;

public static class PyBuiltins
{
    private static readonly Dictionary<string, PyCallable> Functions = new(StringComparer.Ordinal)
    {
        ["print"] = Fn("print", args => { Console.WriteLine(string.Join(" ", args.Select(x => x.ToPythonString()))); return PyValue.None; }),
        ["len"] = Fn("len", Length),
        ["hash"] = Fn("hash", args => { Arity(args, 1, "hash"); return PyValue.From(PyOperations.Hash(args[0])); }),
        ["bool"] = Fn("bool", args => PyValue.From(args.Count == 0 ? false : args[0].IsTruthy())),
        ["str"] = Fn("str", args => PyValue.From(args.Count == 0 ? "" : args[0].ToPythonString())),
        ["repr"] = Fn("repr", args => { Arity(args,1,"repr"); return PyValue.From(args[0].Repr()); }),
        ["int"] = Fn("int", args => { if(args.Count==0)return PyValue.From(0); var x=args[0]; if(x.Type==PyType.Integer)return x; if(x.Type==PyType.Boolean)return PyValue.From((bool)x.Data! ? 1 : 0); if(x.Type==PyType.Float)return PyValue.From(new BigInt((double)x.Data!)); if(x.Type==PyType.String && BigInt.TryParse((string)x.Data!,out var n))return PyValue.From(n); throw new PythonException("ValueError","invalid literal for int()"); }),
        ["float"] = Fn("float", args => { if(args.Count==0)return PyValue.From(0d); var x=args[0]; if(x.Type==PyType.Float)return x; if(x.Type==PyType.Integer)return PyValue.From((double)(BigInt)x.Data!); if(x.Type==PyType.Boolean)return PyValue.From((bool)x.Data!?1d:0d); if(x.Type==PyType.String && double.TryParse((string)x.Data!,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var d))return PyValue.From(d); throw new PythonException("ValueError","could not convert string to float"); }),
        ["range"] = Fn("range", Range),
        ["staticmethod"] = PyMethodDescriptor.Constructor(bindsClass: false),
        ["classmethod"] = PyMethodDescriptor.Constructor(bindsClass: true),
        ["property"] = PyProperty.Constructor,
        ["bytes"] = PyBytesConstructor.Callable,
        ["set"] = Fn("set", args => args.Count switch
        {
            0 => PyValue.From(new PySet()),
            1 => PyValue.From(new PySet(PyOperations.Iterate(args[0]))),
            _ => throw new PythonException("TypeError", $"set expected at most 1 argument, got {args.Count}")
        }),
        ["list"] = Fn("list", args => PyValue.From(args.Count==0?new PyList():new PyList(PyOperations.Iterate(args[0])))),
        ["tuple"] = Fn("tuple", args => PyValue.From(new PyTuple(args.Count==0?[]:PyOperations.Iterate(args[0])))),
        ["dict"] = Fn("dict", args => args.Count==0?PyValue.From(new PyDict()):throw new PythonException("TypeError","dict() argument form is not supported")),
        ["iter"] = Fn("iter", args =>
        {
            Arity(args, 1, "iter");
            return PyValue.From(PyOperations.GetIterator(args[0]));
        }),
        ["next"] = Fn("next", args =>
        {
            if (args.Count is < 1 or > 2)
                throw new PythonException("TypeError", "next() expected an iterator and optional default");
            if (args[0].Data is not PyIterator iterator)
                throw new PythonException("TypeError", $"'{PyOperations.TypeName(args[0])}' object is not an iterator");
            return iterator.Next(out var result) ?? (args.Count == 2 ? args[1] : throw Runtime.PyExceptions.StopIteration(result));
        }),
        ["abs"] = Fn("abs", args => { Arity(args,1,"abs"); return args[0].Type switch { PyType.Integer=>PyValue.From(BigInt.Abs((BigInt)args[0].Data!)), PyType.Float=>PyValue.From(Math.Abs((double)args[0].Data!)), PyType.Temporal when PyDateTimeModule.Unary("abs", (PyDateTimeModule.Value)args[0].Data!) is { } result => result, _=>throw new PythonException("TypeError","bad operand type for abs()") }; }),
        ["sum"] = Fn("sum", args => { if(args.Count is < 1 or > 2)throw new PythonException("TypeError","sum expected iterable and optional start"); var total=args.Count==2?args[1]:PyValue.From(0); foreach(var item in PyOperations.Iterate(args[0]))total=PyOperations.Binary(total,"+",item); return total; }),
        ["min"] = Fn("min", args => Aggregate(args,"<")), ["max"] = Fn("max", args => Aggregate(args,">")),
        ["sorted"] = Fn("sorted", args => { Arity(args,1,"sorted"); var items=PyOperations.Iterate(args[0]).ToList(); items.Sort((a,b)=>PyOperations.Compare(a,"<",b)?-1:PyOperations.Compare(a,">",b)?1:0); return PyValue.From(new PyList(items)); }),
    };
    public static PyValue Get(string name) => Functions.TryGetValue(name,out var fn) ? PyValue.From(fn) : throw new PythonException("NameError",$"name '{name}' is not defined");
    public static PyValue Call(string name,IReadOnlyList<PyValue> arguments) => Get(name).Call(arguments);
    public static bool Contains(string name) => Functions.ContainsKey(name);
    private static PyValue Length(IReadOnlyList<PyValue> args)
    {
        Arity(args, 1, "len");
        var value = args[0];
        if (value.Data is PyRange range)
        {
            if (range.Length > new BigInt((long)nint.MaxValue))
                throw new PythonException("OverflowError", "Python int too large to convert to C ssize_t");
            return PyValue.From(range.Length);
        }
        return PyValue.From(value.Type switch
        {
            PyType.String => ((string)value.Data!).EnumerateRunes().Count(),
            PyType.Bytes => ((PyBytes)value.Data!).Count,
            PyType.List => ((PyList)value.Data!).Count,
            PyType.Tuple => ((PyTuple)value.Data!).Count,
            PyType.Dictionary => ((PyDict)value.Data!).Count,
            PyType.Set => ((PySet)value.Data!).Count,
            _ => throw new PythonException("TypeError", $"object of type '{PyOperations.TypeName(value)}' has no len()")
        });
    }

    private static PyValue Range(IReadOnlyList<PyValue> args)
    {
        if (args.Count == 0) throw new PythonException("TypeError", "range expected at least 1 argument, got 0");
        if (args.Count > 3) throw new PythonException("TypeError", $"range expected at most 3 arguments, got {args.Count}");
        BigInt start = args.Count == 1 ? BigInt.Zero : RangeArgument(args[0]);
        BigInt stop = RangeArgument(args[args.Count == 1 ? 0 : 1]);
        BigInt step = args.Count == 3 ? RangeArgument(args[2]) : BigInt.One;
        return PyValue.From(new PyRange(start, stop, step));
    }

    private static BigInt RangeArgument(PyValue value) => value.Type switch
    {
        PyType.Integer => (BigInt)value.Data!,
        PyType.Boolean => value.IsTruthy() ? BigInt.One : BigInt.Zero,
        _ => throw new PythonException("TypeError", $"'{PyOperations.TypeName(value)}' object cannot be interpreted as an integer")
    };

    private static PyCallable Fn(string name,Func<IReadOnlyList<PyValue>,PyValue> body)=>new(body,name);
    private static void Arity(IReadOnlyList<PyValue> args,int count,string name) { if(args.Count!=count)throw new PythonException("TypeError",$"{name} expected {count} argument(s), got {args.Count}"); }
    private static PyValue Aggregate(IReadOnlyList<PyValue> args,string op) { var items=args.Count==1?PyOperations.Iterate(args[0]).ToList():args.ToList(); if(items.Count==0)throw new PythonException("ValueError",$"{op}() arg is an empty sequence"); return items.Aggregate((a,b)=>PyOperations.Compare(b,op,a)?b:a); }
}
