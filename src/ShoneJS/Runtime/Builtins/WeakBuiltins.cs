// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;

namespace ShoneJS.Runtime.Builtins;

/// <summary>Installs weak collections and weak references backed by runtime weak-key storage.</summary>
public static class WeakBuiltins
{
    public static void Install(Action<string, JsValue> setGlobal, JsObject objectPrototype, Func<JsSymbol, bool>? isRegisteredSymbol = null)
    {
        isRegisteredSymbol ??= _ => false;
        InstallWeakMap(setGlobal, objectPrototype, isRegisteredSymbol);
        InstallWeakSet(setGlobal, objectPrototype, isRegisteredSymbol);
        InstallWeakRef(setGlobal, objectPrototype, isRegisteredSymbol);
    }

    private static void InstallWeakMap(Action<string, JsValue> setGlobal, JsObject objectPrototype, Func<JsSymbol, bool> isRegisteredSymbol)
    {
        var prototype = new JsObject { Prototype = objectPrototype };
        var constructor = CreateConstructor("WeakMap", 0, objectPrototype, prototype, args =>
        {
            if (args.Length > 0 && !AbstractOperations.IsNullish(args[0]))
                throw JsException.TypeError("WeakMap iterable initialization is unsupported");
            return JsValue.From(new WeakMapObject(isRegisteredSymbol) { Prototype = prototype });
        });
        DefineMethod(prototype, "delete", 1, (receiver, args) => JsValue.From(Require<WeakMapObject>(receiver).Delete(Argument(args, 0))));
        DefineMethod(prototype, "get", 1, (receiver, args) => Require<WeakMapObject>(receiver).Get(Argument(args, 0)));
        DefineMethod(prototype, "has", 1, (receiver, args) => JsValue.From(Require<WeakMapObject>(receiver).Has(Argument(args, 0))));
        DefineMethod(prototype, "set", 2, (receiver, args) =>
        {
            var map = Require<WeakMapObject>(receiver);
            map.Set(Argument(args, 0), Argument(args, 1));
            return receiver;
        });
        setGlobal("WeakMap", JsValue.From(constructor));
    }

    private static void InstallWeakSet(Action<string, JsValue> setGlobal, JsObject objectPrototype, Func<JsSymbol, bool> isRegisteredSymbol)
    {
        var prototype = new JsObject { Prototype = objectPrototype };
        var constructor = CreateConstructor("WeakSet", 0, objectPrototype, prototype, args =>
        {
            if (args.Length > 0 && !AbstractOperations.IsNullish(args[0]))
                throw JsException.TypeError("WeakSet iterable initialization is unsupported");
            return JsValue.From(new WeakSetObject(isRegisteredSymbol) { Prototype = prototype });
        });
        DefineMethod(prototype, "add", 1, (receiver, args) =>
        {
            var set = Require<WeakSetObject>(receiver);
            set.Add(Argument(args, 0));
            return receiver;
        });
        DefineMethod(prototype, "delete", 1, (receiver, args) => JsValue.From(Require<WeakSetObject>(receiver).Delete(Argument(args, 0))));
        DefineMethod(prototype, "has", 1, (receiver, args) => JsValue.From(Require<WeakSetObject>(receiver).Has(Argument(args, 0))));
        setGlobal("WeakSet", JsValue.From(constructor));
    }

    private static void InstallWeakRef(Action<string, JsValue> setGlobal, JsObject objectPrototype, Func<JsSymbol, bool> isRegisteredSymbol)
    {
        var prototype = new JsObject { Prototype = objectPrototype };
        var constructor = new JsFunction(_ => throw JsException.TypeError("WeakRef must be constructed with new"), 1)
        {
            Prototype = objectPrototype,
            NativeConstruct = args => JsValue.From(new WeakRefObject(RequireWeakKey(Argument(args, 0), isRegisteredSymbol), prototype))
        };
        SetName(constructor, "WeakRef");
        DefineOwn(constructor, "prototype", JsValue.From(prototype));
        DefineMethod(prototype, "deref", 0, (receiver, _) => Require<WeakRefObject>(receiver).Deref());
        setGlobal("WeakRef", JsValue.From(constructor));
    }

    private static JsFunction CreateConstructor(string name, int arity, JsObject objectPrototype, JsObject prototype, Func<JsValue[], JsValue> construct)
    {
        var constructor = new JsFunction(_ => throw JsException.TypeError($"{name} must be constructed with new"), arity)
        {
            Prototype = objectPrototype,
            NativeConstruct = construct
        };
        SetName(constructor, name);
        DefineOwn(constructor, "prototype", JsValue.From(prototype));
        return constructor;
    }

    private static void DefineMethod(JsObject target, string name, int arity, Func<JsValue, JsValue[], JsValue> call)
    {
        var method = new JsFunction(call, arity) { Prototype = target.GetPrototypeOf() };
        SetName(method, name);
        DefineOwn(target, name, JsValue.From(method));
    }

    private static void SetName(JsFunction function, string name) =>
        function.DefineOwnProperty("name", PropertyDescriptor.Data(JsValue.From(name), configurable: true));

    private static void DefineOwn(JsObject target, string name, JsValue value) =>
        target.DefineOwnProperty(name, PropertyDescriptor.Data(value, writable: true, configurable: true));

    private static JsValue Argument(JsValue[] args, int index) => index < args.Length ? args[index] : JsValue.Undefined;

    private static T Require<T>(JsValue receiver) where T : JsObject =>
        receiver.Data as T ?? throw JsException.TypeError("Incompatible receiver");

    // Registered symbols are strongly reachable through the registry and cannot be weak keys.
    private static object RequireWeakKey(JsValue value, Func<JsSymbol, bool> isRegisteredSymbol)
    {
        if (value.Data is JsObject obj) return obj;
        if (value.Data is JsSymbol symbol && !isRegisteredSymbol(symbol)) return symbol;
        throw JsException.TypeError("Invalid value used as weak key");
    }

    private static object? WeakKeyOrNull(JsValue value, Func<JsSymbol, bool> isRegisteredSymbol)
    {
        if (value.Data is JsObject obj) return obj;
        if (value.Data is JsSymbol symbol && !isRegisteredSymbol(symbol)) return symbol;
        return null;
    }

    private sealed class WeakMapObject(Func<JsSymbol, bool> isRegisteredSymbol) : JsObject
    {
        private readonly ConditionalWeakTable<object, Box> _entries = new();
        private sealed class Box(JsValue value) { public JsValue Value = value; }
        public bool Has(JsValue value) => WeakKeyOrNull(value, isRegisteredSymbol) is { } key && _entries.TryGetValue(key, out _);
        public JsValue Get(JsValue value) => WeakKeyOrNull(value, isRegisteredSymbol) is { } key && _entries.TryGetValue(key, out var box) ? box.Value : JsValue.Undefined;
        public bool Delete(JsValue value) => WeakKeyOrNull(value, isRegisteredSymbol) is { } key && _entries.Remove(key);
        public void Set(JsValue value, JsValue stored)
        {
            var key = RequireWeakKey(value, isRegisteredSymbol);
            _entries.Remove(key);
            _entries.Add(key, new Box(stored));
        }
    }

    private sealed class WeakSetObject(Func<JsSymbol, bool> isRegisteredSymbol) : JsObject
    {
        private readonly ConditionalWeakTable<object, Marker> _entries = new();
        private sealed class Marker;
        public bool Has(JsValue value) => WeakKeyOrNull(value, isRegisteredSymbol) is { } key && _entries.TryGetValue(key, out _);
        public bool Delete(JsValue value) => WeakKeyOrNull(value, isRegisteredSymbol) is { } key && _entries.Remove(key);
        public void Add(JsValue value)
        {
            var key = RequireWeakKey(value, isRegisteredSymbol);
            if (!_entries.TryGetValue(key, out _)) _entries.Add(key, new Marker());
        }
    }

    private sealed class WeakRefObject : JsObject
    {
        private readonly WeakReference<object> _target;
        public WeakRefObject(object target, JsObject prototype) { _target = new WeakReference<object>(target); Prototype = prototype; }
        public JsValue Deref() => _target.TryGetTarget(out var target) ? target switch
        {
            JsObject obj => JsValue.From(obj),
            JsSymbol symbol => JsValue.From(symbol),
            _ => JsValue.Undefined
        } : JsValue.Undefined;
    }
}
