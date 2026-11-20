// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Globalization;

namespace ShoneJs.Runtime.Builtins;

public static class ProxyBuiltins
{
    public static void Install(Action<string, JsValue> setGlobal, JsObject objectPrototype)
    {
        var constructor = new JsFunction(_ => throw JsException.TypeError("Proxy must be constructed with new"), 2)
        {
            Prototype = objectPrototype,
            NativeConstruct = args => JsValue.From(Create(args, objectPrototype))
        };
        constructor.DefineOwnProperty("name", PropertyDescriptor.Data(JsValue.From("Proxy"), configurable: true));

        var revocable = new JsFunction(args =>
        {
            var proxy = Create(args, objectPrototype);
            ProxyObject? revocableProxy = proxy;
            var revoke = new JsFunction(_ =>
            {
                revocableProxy?.Revoke();
                revocableProxy = null;
                return JsValue.Undefined;
            }) { Prototype = objectPrototype };
            revoke.DefineOwnProperty("name", PropertyDescriptor.Data(JsValue.From(""), configurable: true));

            var result = new JsObject { Prototype = objectPrototype };
            CreateDataProperty(result, "proxy", JsValue.From(proxy));
            CreateDataProperty(result, "revoke", JsValue.From(revoke));
            return JsValue.From(result);
        }, 2) { Prototype = objectPrototype };
        revocable.DefineOwnProperty("name", PropertyDescriptor.Data(JsValue.From("revocable"), configurable: true));
        constructor.DefineOwnProperty("revocable", PropertyDescriptor.Data(JsValue.From(revocable), writable: true, configurable: true));
        setGlobal("Proxy", JsValue.From(constructor));
    }

    internal static bool IsArray(JsObject? value)
    {
        while (value is ProxyObject proxy)
            value = proxy.GetTarget();
        return value is JsArray;
    }

    private static ProxyObject Create(JsValue[] args, JsObject objectPrototype)
    {
        if (args.Length < 2 || args[0].Data is not JsObject target || args[1].Data is not JsObject handler)
            throw JsException.TypeError("Proxy target and handler must be objects");
        if (target is JsFunction)
            throw JsException.TypeError("Callable Proxy targets are unsupported");
        return new ProxyObject(target, handler, objectPrototype);
    }

    private static JsValue PrototypeValue(JsObject? prototype) => prototype is null ? JsValue.Null : JsValue.From(prototype);

    private static void CreateDataProperty(JsObject obj, string key, JsValue value) =>
        obj.DefineOwnProperty(key, PropertyDescriptor.Data(value, writable: true, enumerable: true, configurable: true));

    private static JsValue FromDescriptor(PropertyDescriptor descriptor, JsObject objectPrototype)
    {
        var result = new JsObject { Prototype = objectPrototype };
        if (descriptor.Value is { } value)
            CreateDataProperty(result, "value", value);
        if (descriptor.Writable is { } writable)
            CreateDataProperty(result, "writable", JsValue.From(writable));
        if (descriptor.Get is { } get)
            CreateDataProperty(result, "get", get);
        if (descriptor.Set is { } set)
            CreateDataProperty(result, "set", set);
        if (descriptor.Enumerable is { } enumerable)
            CreateDataProperty(result, "enumerable", JsValue.From(enumerable));
        if (descriptor.Configurable is { } configurable)
            CreateDataProperty(result, "configurable", JsValue.From(configurable));
        return JsValue.From(result);
    }

    private sealed class ProxyObject(JsObject target, JsObject handler, JsObject objectPrototype) : JsObject
    {
        private JsObject? _target = target;
        private JsObject? _handler = handler;

        internal override bool HasOrdinaryPrototypeLookup => false;

        public JsObject GetTarget() => _target ?? throw JsException.TypeError("Cannot inspect a revoked Proxy");

        public void Revoke()
        {
            _target = null;
            _handler = null;
        }

        private (JsObject Target, JsValue Handler, JsFunction? Trap) GetTrap(string name)
        {
            // A handler getter may revoke this proxy, so retain both objects before reading the trap.
            var target = _target ?? throw JsException.TypeError($"Cannot perform '{name}' on a revoked Proxy");
            var handler = _handler!;
            var trap = handler.Get(name);
            if (AbstractOperations.IsNullish(trap))
                return (target, JsValue.From(handler), null);
            if (trap.Data is not JsFunction function)
                throw JsException.TypeError($"Proxy trap '{name}' must be callable");
            return (target, JsValue.From(handler), function);
        }

        public override JsObject? GetPrototypeOf()
        {
            var (target, handler, trap) = GetTrap("getPrototypeOf");
            if (trap is null)
                return target.GetPrototypeOf();

            var result = trap.Call(handler, [JsValue.From(target)]);
            var prototype = result.Data as JsObject;
            if (prototype is null && result.Type != JsType.Null)
                throw JsException.TypeError("Proxy trap 'getPrototypeOf' must return an object or null");
            if (!target.IsExtensible && !ReferenceEquals(prototype, target.GetPrototypeOf()))
                throw JsException.TypeError("Proxy trap 'getPrototypeOf' must report the prototype of a non-extensible target");
            return prototype;
        }

        public override bool SetPrototypeOf(JsObject? prototype)
        {
            var (target, handler, trap) = GetTrap("setPrototypeOf");
            if (trap is null)
                return target.SetPrototypeOf(prototype);
            if (!trap.Call(handler, [JsValue.From(target), PrototypeValue(prototype)]).ToBoolean())
                return false;
            if (!target.IsExtensible && !ReferenceEquals(prototype, target.GetPrototypeOf()))
                throw JsException.TypeError("Proxy trap 'setPrototypeOf' cannot change the prototype of a non-extensible target");
            return true;
        }

        public override bool IsExtensible
        {
            get
            {
                var (target, handler, trap) = GetTrap("isExtensible");
                if (trap is null)
                    return target.IsExtensible;
                var result = trap.Call(handler, [JsValue.From(target)]).ToBoolean();
                if (result != target.IsExtensible)
                    throw JsException.TypeError("Proxy trap 'isExtensible' must report the target's actual extensibility");
                return result;
            }
        }

        public override bool PreventExtensions()
        {
            var (target, handler, trap) = GetTrap("preventExtensions");
            if (trap is null)
                return target.PreventExtensions();
            if (!trap.Call(handler, [JsValue.From(target)]).ToBoolean())
                return false;
            if (target.IsExtensible)
                throw JsException.TypeError("Proxy trap 'preventExtensions' cannot report success for an extensible target");
            return true;
        }

        public override PropertyDescriptor? GetOwnProperty(string key)
        {
            var (target, handler, trap) = GetTrap("getOwnPropertyDescriptor");
            if (trap is null)
                return target.GetOwnProperty(key);

            var result = trap.Call(handler, [JsValue.From(target), JsValue.From(key)]);
            if (result.Type != JsType.Undefined && result.Data is not JsObject)
                throw JsException.TypeError("Proxy trap 'getOwnPropertyDescriptor' must return an object or undefined");
            var current = target.GetOwnProperty(key);
            if (result.Type == JsType.Undefined)
            {
                if (current is not null && (current.Configurable == false || !target.IsExtensible))
                    throw JsException.TypeError("Proxy trap 'getOwnPropertyDescriptor' cannot hide the target property");
                return null;
            }

            var extensible = target.IsExtensible;
            var descriptor = ObjectBuiltins.ToDescriptor(result).Complete();
            if (current is null ? !extensible : !descriptor.IsCompatibleWith(current))
                throw JsException.TypeError("Proxy trap 'getOwnPropertyDescriptor' returned an incompatible descriptor");
            if (descriptor.Configurable == false && current?.Configurable != false)
                throw JsException.TypeError("Proxy trap 'getOwnPropertyDescriptor' cannot report a new non-configurable property");
            if (descriptor is { Configurable: false, Writable: false } && current?.Writable == true)
                throw JsException.TypeError("Proxy trap 'getOwnPropertyDescriptor' cannot report a writable property as read-only");
            return descriptor;
        }

        public override bool DefineOwnProperty(string key, PropertyDescriptor descriptor)
        {
            var (target, handler, trap) = GetTrap("defineProperty");
            if (trap is null)
                return target.DefineOwnProperty(key, descriptor);
            descriptor.Validate();
            if (!trap.Call(handler, [JsValue.From(target), JsValue.From(key), FromDescriptor(descriptor, objectPrototype)]).ToBoolean())
                return false;

            var current = target.GetOwnProperty(key);
            var extensible = target.IsExtensible;
            if (current is null ? !extensible : !descriptor.IsCompatibleWith(current))
                throw JsException.TypeError("Proxy trap 'defineProperty' reported success for an incompatible descriptor");
            if (descriptor.Configurable == false && current?.Configurable != false)
                throw JsException.TypeError("Proxy trap 'defineProperty' cannot report success creating a new non-configurable property");
            if (current is { Configurable: false, Writable: true } && descriptor.Writable == false)
                throw JsException.TypeError("Proxy trap 'defineProperty' cannot report success making a writable property read-only");
            return true;
        }

        public override bool HasProperty(string key)
        {
            var (target, handler, trap) = GetTrap("has");
            if (trap is null)
                return target.HasProperty(key);
            if (trap.Call(handler, [JsValue.From(target), JsValue.From(key)]).ToBoolean())
                return true;
            if (target.GetOwnProperty(key) is { } current && (current.Configurable == false || !target.IsExtensible))
                throw JsException.TypeError("Proxy trap 'has' cannot hide the target property");
            return false;
        }

        public override JsValue Get(string key, JsValue receiver)
        {
            var (target, handler, trap) = GetTrap("get");
            if (trap is null)
                return target.Get(key, receiver);
            var result = trap.Call(handler, [JsValue.From(target), JsValue.From(key), receiver]);
            if (target.GetOwnProperty(key) is { Configurable: false } current)
            {
                if (current.Writable == false && !AbstractOperations.SameValue(result, current.Value!.Value))
                    throw JsException.TypeError("Proxy trap 'get' must return the value of a frozen data property");
                if (current.Get is { Type: JsType.Undefined } && result.Type != JsType.Undefined)
                    throw JsException.TypeError("Proxy trap 'get' must return undefined for an accessor without a getter");
            }
            return result;
        }

        public override bool Set(string key, JsValue value, JsValue receiver)
        {
            var (target, handler, trap) = GetTrap("set");
            if (trap is null)
                return target.Set(key, value, receiver);
            if (!trap.Call(handler, [JsValue.From(target), JsValue.From(key), value, receiver]).ToBoolean())
                return false;
            if (target.GetOwnProperty(key) is { Configurable: false } current)
            {
                if (current.Writable == false && !AbstractOperations.SameValue(value, current.Value!.Value))
                    throw JsException.TypeError("Proxy trap 'set' cannot change the value of a frozen data property");
                if (current.Set is { Type: JsType.Undefined })
                    throw JsException.TypeError("Proxy trap 'set' cannot report success for an accessor without a setter");
            }
            return true;
        }

        public override bool Delete(string key)
        {
            var (target, handler, trap) = GetTrap("deleteProperty");
            if (trap is null)
                return target.Delete(key);
            if (!trap.Call(handler, [JsValue.From(target), JsValue.From(key)]).ToBoolean())
                return false;
            if (target.GetOwnProperty(key) is { } current && (current.Configurable == false || !target.IsExtensible))
                throw JsException.TypeError("Proxy trap 'deleteProperty' cannot report deleting the target property");
            return true;
        }

        public override IEnumerable<string> OwnPropertyKeys()
        {
            var (target, handler, trap) = GetTrap("ownKeys");
            if (trap is null)
                return target.OwnPropertyKeys();

            if (trap.Call(handler, [JsValue.From(target)]).Data is not JsObject result)
                throw JsException.TypeError("Proxy trap 'ownKeys' must return an array-like object");
            var length = AbstractOperations.ToPrimitive(result.Get("length")).ToNumber();
            length = double.IsNaN(length) ? 0 : Math.Clamp(Math.Truncate(length), 0, 9_007_199_254_740_991d);
            var keys = new List<string>();
            for (long i = 0; i < length; i++)
            {
                if (result.Get(i.ToString(CultureInfo.InvariantCulture)).Data is not string key)
                    throw JsException.TypeError("Proxy trap 'ownKeys' may only return string keys in this runtime");
                keys.Add(key);
            }

            // Check for duplicates after converting the entire array-like result, since index getters can have side effects.
            var uncheckedKeys = new HashSet<string>(keys, StringComparer.Ordinal);
            if (uncheckedKeys.Count != keys.Count)
                throw JsException.TypeError("Proxy trap 'ownKeys' returned duplicate keys");

            var extensible = target.IsExtensible;
            var targetKeys = target.OwnPropertyKeys().ToArray();
            var nonConfigurableKeys = new List<string>();
            var otherKeys = new List<string>();
            foreach (var key in targetKeys)
            {
                if (target.GetOwnProperty(key)?.Configurable == false)
                    nonConfigurableKeys.Add(key);
                else
                    otherKeys.Add(key);
            }
            foreach (var key in nonConfigurableKeys)
            {
                if (!uncheckedKeys.Remove(key))
                    throw JsException.TypeError("Proxy trap 'ownKeys' must include all non-configurable keys");
            }
            if (!extensible)
            {
                foreach (var key in otherKeys)
                {
                    if (!uncheckedKeys.Remove(key))
                        throw JsException.TypeError("Proxy trap 'ownKeys' must include all keys of a non-extensible target");
                }
                if (uncheckedKeys.Count != 0)
                    throw JsException.TypeError("Proxy trap 'ownKeys' cannot add keys to a non-extensible target");
            }
            return keys;
        }
    }
}
