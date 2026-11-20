// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Globalization;

namespace ShoneJS.Runtime.Builtins;

public static class ObjectBuiltins
{
    public static JsObject Install(Action<string, JsValue> setGlobal, Func<JsObject?>? arrayPrototype = null)
    {
        var realm = new ObjectBuiltinsRealm(arrayPrototype);
        realm.Install(setGlobal);
        return realm.ObjectPrototype;
    }

    internal static string ToPropertyKey(JsValue value) =>
        AbstractOperations.ToPrimitive(value, preferString: true).ToJsString();

    private sealed class ObjectPrototypeObject : JsObject
    {
        public override bool SetPrototypeOf(JsObject? prototype) => prototype is null;
    }

    private sealed class ObjectBuiltinsRealm(Func<JsObject?>? arrayPrototype)
    {
        public JsObject ObjectPrototype { get; } = new ObjectPrototypeObject();

        public void Install(Action<string, JsValue> setGlobal)
        {
            var constructor = new JsFunction(args =>
            {
                var value = Argument(args, 0);
                return JsValue.From(AbstractOperations.IsNullish(value) ? NewObject() : ToObject(value));
            }, 1) { Prototype = ObjectPrototype, DefaultConstructionPrototype = ObjectPrototype };
            constructor.DefineOwnProperty("name", PropertyDescriptor.Data(JsValue.From("Object"), configurable: true));
            constructor.DefineOwnProperty("prototype", PropertyDescriptor.Data(JsValue.From(ObjectPrototype)));
            ObjectPrototype.DefineOwnProperty("constructor", PropertyDescriptor.Data(JsValue.From(constructor), writable: true, configurable: true));

            DefineMethod(ObjectPrototype, "hasOwnProperty", 1, (receiver, args) =>
            {
                var key = ToPropertyKey(Argument(args, 0));
                return JsValue.From(ToObject(receiver).HasOwn(key));
            });
            DefineMethod(ObjectPrototype, "propertyIsEnumerable", 1, (receiver, args) =>
            {
                var key = ToPropertyKey(Argument(args, 0));
                return JsValue.From(ToObject(receiver).GetOwnProperty(key)?.Enumerable == true);
            });
            DefineMethod(ObjectPrototype, "isPrototypeOf", 1, (receiver, args) =>
            {
                if (Argument(args, 0).Data is not JsObject obj)
                    return JsValue.From(false);
                var prototype = ToObject(receiver);
                for (var current = obj.GetPrototypeOf(); current is not null; current = current.GetPrototypeOf())
                    if (ReferenceEquals(current, prototype))
                        return JsValue.From(true);
                return JsValue.From(false);
            });
            DefineMethod(ObjectPrototype, "toString", 0, (receiver, _) => ObjectToString(receiver));
            DefineMethod(ObjectPrototype, "toLocaleString", 0, (receiver, _) =>
            {
                var obj = ToObject(receiver);
                if (obj.Get("toString") is not { Data: JsFunction method })
                    throw JsException.TypeError("toString is not callable");
                return method.Call(receiver, []);
            });
            DefineMethod(ObjectPrototype, "valueOf", 0, (receiver, _) => JsValue.From(ToObject(receiver)));

            DefineMethod(constructor, "assign", 2, Assign);
            DefineMethod(constructor, "create", 2, args =>
            {
                var result = new JsObject { Prototype = ToPrototype(Argument(args, 0)) };
                var properties = Argument(args, 1);
                if (properties.Type != JsType.Undefined)
                    DefineProperties(result, properties);
                return JsValue.From(result);
            });
            DefineMethod(constructor, "defineProperties", 2, args => DefineProperties(RequireObject(Argument(args, 0)), Argument(args, 1)));
            DefineMethod(constructor, "defineProperty", 3, args =>
            {
                var obj = RequireObject(Argument(args, 0));
                var key = ToPropertyKey(Argument(args, 1));
                DefineOrThrow(obj, key, ToDescriptor(Argument(args, 2)));
                return JsValue.From(obj);
            });
            DefineMethod(constructor, "entries", 1, args => CollectEnumerable(args, (obj, key) => MakeArray([JsValue.From(key), obj.Get(key)])));
            DefineMethod(constructor, "freeze", 1, args => SetIntegrityLevel(Argument(args, 0), freeze: true));
            DefineMethod(constructor, "getOwnPropertyDescriptor", 2, args => GetOwnPropertyDescriptor(ToObject(Argument(args, 0)), Argument(args, 1)));
            DefineMethod(constructor, "getOwnPropertyDescriptors", 1, args => GetOwnPropertyDescriptors(ToObject(Argument(args, 0))));
            DefineMethod(constructor, "getOwnPropertyNames", 1, args => OwnKeys(ToObject(Argument(args, 0))));
            DefineMethod(constructor, "getOwnPropertySymbols", 1, args =>
            {
                _ = ToObject(Argument(args, 0)).OwnPropertyKeys().ToArray();
                return MakeArray([]);
            });
            DefineMethod(constructor, "getPrototypeOf", 1, args => PrototypeValue(ToObject(Argument(args, 0))));
            DefineMethod(constructor, "is", 2, args => JsValue.From(AbstractOperations.SameValue(Argument(args, 0), Argument(args, 1))));
            DefineMethod(constructor, "isExtensible", 1, args => JsValue.From(Argument(args, 0).Data is JsObject { IsExtensible: true }));
            DefineMethod(constructor, "isFrozen", 1, args => JsValue.From(TestIntegrityLevel(Argument(args, 0), frozen: true)));
            DefineMethod(constructor, "isSealed", 1, args => JsValue.From(TestIntegrityLevel(Argument(args, 0), frozen: false)));
            DefineMethod(constructor, "keys", 1, args => CollectEnumerable(args, (_, key) => JsValue.From(key)));
            DefineMethod(constructor, "preventExtensions", 1, args =>
            {
                var value = Argument(args, 0);
                if (value.Data is JsObject obj && !obj.PreventExtensions())
                    throw JsException.TypeError("Cannot prevent extensions");
                return value;
            });
            DefineMethod(constructor, "seal", 1, args => SetIntegrityLevel(Argument(args, 0), freeze: false));
            DefineMethod(constructor, "setPrototypeOf", 2, args =>
            {
                var target = Argument(args, 0);
                if (AbstractOperations.IsNullish(target))
                    throw JsException.TypeError("Cannot set the prototype of a nullish value");
                var prototype = ToPrototype(Argument(args, 1));
                if (target.Data is JsObject obj && !obj.SetPrototypeOf(prototype))
                    throw JsException.TypeError("Cannot change the object's prototype");
                return target;
            });
            DefineMethod(constructor, "values", 1, args => CollectEnumerable(args, (obj, key) => obj.Get(key)));
            setGlobal("Object", JsValue.From(constructor));

            var reflect = NewObject();
            DefineMethod(reflect, "apply", 3, args =>
            {
                if (Argument(args, 0).Data is not JsFunction target)
                    throw JsException.TypeError("Reflect.apply requires a callable target");
                return target.Call(Argument(args, 1), ReadArgumentList(Argument(args, 2)));
            });
            DefineMethod(reflect, "construct", 2, args =>
            {
                if (Argument(args, 0).Data is not JsFunction { IsConstructor: true } target)
                    throw JsException.TypeError("Reflect.construct requires a constructor target");
                var newTarget = args.Length < 3 ? target : args[2].Data as JsFunction;
                if (newTarget is not { IsConstructor: true })
                    throw JsException.TypeError("Reflect.construct requires a constructor newTarget");
                return target.Construct(ReadArgumentList(Argument(args, 1)), newTarget);
            });
            DefineMethod(reflect, "defineProperty", 3, args => JsValue.From(RequireObject(Argument(args, 0)).DefineOwnProperty(ToPropertyKey(Argument(args, 1)), ToDescriptor(Argument(args, 2)))));
            DefineMethod(reflect, "deleteProperty", 2, args => JsValue.From(RequireObject(Argument(args, 0)).Delete(ToPropertyKey(Argument(args, 1)))));
            DefineMethod(reflect, "get", 2, args =>
            {
                var target = RequireObject(Argument(args, 0));
                return target.Get(ToPropertyKey(Argument(args, 1)), args.Length > 2 ? args[2] : args[0]);
            });
            DefineMethod(reflect, "getOwnPropertyDescriptor", 2, args => GetOwnPropertyDescriptor(RequireObject(Argument(args, 0)), Argument(args, 1)));
            DefineMethod(reflect, "getPrototypeOf", 1, args => PrototypeValue(RequireObject(Argument(args, 0))));
            DefineMethod(reflect, "has", 2, args => JsValue.From(RequireObject(Argument(args, 0)).HasProperty(ToPropertyKey(Argument(args, 1)))));
            DefineMethod(reflect, "isExtensible", 1, args => JsValue.From(RequireObject(Argument(args, 0)).IsExtensible));
            DefineMethod(reflect, "ownKeys", 1, args => OwnKeys(RequireObject(Argument(args, 0))));
            DefineMethod(reflect, "preventExtensions", 1, args => JsValue.From(RequireObject(Argument(args, 0)).PreventExtensions()));
            DefineMethod(reflect, "set", 3, args =>
            {
                var target = RequireObject(Argument(args, 0));
                return JsValue.From(target.Set(ToPropertyKey(Argument(args, 1)), Argument(args, 2), args.Length > 3 ? args[3] : args[0]));
            });
            DefineMethod(reflect, "setPrototypeOf", 2, args => JsValue.From(RequireObject(Argument(args, 0)).SetPrototypeOf(ToPrototype(Argument(args, 1)))));
            setGlobal("Reflect", JsValue.From(reflect));
        }

        private void DefineMethod(JsObject target, string name, int arity, Func<JsValue[], JsValue> call) =>
            DefineMethod(target, name, arity, (_, args) => call(args));

        private void DefineMethod(JsObject target, string name, int arity, Func<JsValue, JsValue[], JsValue> call)
        {
            var function = new JsFunction(call, arity) { Prototype = ObjectPrototype };
            function.DefineOwnProperty("name", PropertyDescriptor.Data(JsValue.From(name), configurable: true));
            target.DefineOwnProperty(name, PropertyDescriptor.Data(JsValue.From(function), writable: true, configurable: true));
        }

        private static JsValue[] ReadArgumentList(JsValue value)
        {
            var source = RequireObject(value);
            var length = AbstractOperations.ToPrimitive(source.Get("length")).ToNumber();
            if (double.IsNaN(length) || length <= 0) return [];
            if (Math.Truncate(length) > 1_000_000)
                throw JsException.RangeError("Argument list is too large");
            var arguments = new JsValue[(int)Math.Truncate(length)];
            for (var i = 0; i < arguments.Length; i++)
                arguments[i] = source.Get(i.ToString(CultureInfo.InvariantCulture));
            return arguments;
        }

        private JsObject NewObject() => new() { Prototype = ObjectPrototype };

        private JsObject ToObject(JsValue value)
        {
            if (value.Data is JsObject obj)
                return obj;
            if (AbstractOperations.IsNullish(value))
                throw JsException.TypeError("Cannot convert a nullish value to an object");

            var wrapper = new PrimitiveWrapper(value) { Prototype = ObjectPrototype };
            if (value.Data is string text)
            {
                for (var i = 0; i < text.Length; i++)
                    wrapper.DefineOwnProperty(i.ToString(CultureInfo.InvariantCulture), PropertyDescriptor.Data(JsValue.From(text[i].ToString()), enumerable: true));
                wrapper.DefineOwnProperty("length", PropertyDescriptor.Data(JsValue.From((double)text.Length)));
            }
            return wrapper;
        }

        private sealed class PrimitiveWrapper(JsValue value) : JsObject
        {
            public JsType PrimitiveType { get; } = value.Type;
        }

        private static JsValue ObjectToString(JsValue value)
        {
            var type = value.Data is PrimitiveWrapper wrapper ? wrapper.PrimitiveType : value.Type;
            var tag = value.Data switch
            {
                JsArray => "Array",
                JsError => "Error",
                _ => type.ToString()
            };
            return JsValue.From($"[object {tag}]");
        }

        private JsValue Assign(JsValue[] args)
        {
            var target = ToObject(Argument(args, 0));
            var receiver = JsValue.From(target);
            for (var i = 1; i < args.Length; i++)
            {
                if (AbstractOperations.IsNullish(args[i]))
                    continue;
                var source = ToObject(args[i]);
                foreach (var key in source.OwnPropertyKeys())
                {
                    if (source.GetOwnProperty(key)?.Enumerable == true && !target.Set(key, source.Get(key), receiver))
                        throw JsException.TypeError($"Cannot assign property '{key}'");
                }
            }
            return receiver;
        }

        private JsValue CollectEnumerable(JsValue[] args, Func<JsObject, string, JsValue> select)
        {
            var source = ToObject(Argument(args, 0));
            var result = new JsArray { Prototype = arrayPrototype?.Invoke() ?? ObjectPrototype };
            foreach (var key in source.OwnPropertyKeys())
            {
                if (source.GetOwnProperty(key)?.Enumerable == true)
                    result.Add(select(source, key));
            }
            return JsValue.From(result);
        }

        private JsValue MakeArray(IEnumerable<JsValue> values)
        {
            var result = new JsArray { Prototype = arrayPrototype?.Invoke() ?? ObjectPrototype };
            foreach (var value in values)
                result.Add(value);
            return JsValue.From(result);
        }

        private JsValue OwnKeys(JsObject obj) => MakeArray(obj.OwnPropertyKeys().Select(JsValue.From));

        private JsValue DefineProperties(JsObject target, JsValue propertiesValue)
        {
            var properties = ToObject(propertiesValue);
            var descriptors = new List<(string Key, PropertyDescriptor Descriptor)>();
            foreach (var key in properties.OwnPropertyKeys())
            {
                if (properties.GetOwnProperty(key)?.Enumerable == true)
                    descriptors.Add((key, ToDescriptor(properties.Get(key))));
            }
            foreach (var (key, descriptor) in descriptors)
                DefineOrThrow(target, key, descriptor);
            return JsValue.From(target);
        }

        private JsValue GetOwnPropertyDescriptor(JsObject obj, JsValue key) =>
            obj.GetOwnProperty(ToPropertyKey(key)) is { } descriptor ? FromDescriptor(descriptor) : JsValue.Undefined;

        private JsValue GetOwnPropertyDescriptors(JsObject obj)
        {
            var result = NewObject();
            foreach (var key in obj.OwnPropertyKeys())
            {
                if (obj.GetOwnProperty(key) is { } descriptor)
                    CreateDataProperty(result, key, FromDescriptor(descriptor));
            }
            return JsValue.From(result);
        }

        private JsValue FromDescriptor(PropertyDescriptor descriptor)
        {
            var result = NewObject();
            if (descriptor.IsDataDescriptor)
            {
                CreateDataProperty(result, "value", descriptor.Value ?? JsValue.Undefined);
                CreateDataProperty(result, "writable", JsValue.From(descriptor.Writable == true));
            }
            else
            {
                CreateDataProperty(result, "get", descriptor.Get ?? JsValue.Undefined);
                CreateDataProperty(result, "set", descriptor.Set ?? JsValue.Undefined);
            }
            CreateDataProperty(result, "enumerable", JsValue.From(descriptor.Enumerable == true));
            CreateDataProperty(result, "configurable", JsValue.From(descriptor.Configurable == true));
            return JsValue.From(result);
        }
    }

    private static JsValue Argument(JsValue[] args, int index) => index < args.Length ? args[index] : JsValue.Undefined;

    private static JsObject RequireObject(JsValue value) => value.Data as JsObject ?? throw JsException.TypeError("Target must be an object");

    private static JsObject? ToPrototype(JsValue value) => value.Type == JsType.Null ? null : RequireObject(value);

    private static JsValue PrototypeValue(JsObject obj) => obj.GetPrototypeOf() is { } prototype ? JsValue.From(prototype) : JsValue.Null;

    private static void DefineOrThrow(JsObject target, string key, PropertyDescriptor descriptor)
    {
        if (!target.DefineOwnProperty(key, descriptor))
            throw JsException.TypeError($"Cannot define property '{key}'");
    }

    private static void CreateDataProperty(JsObject obj, string key, JsValue value) =>
        DefineOrThrow(obj, key, PropertyDescriptor.Data(value, writable: true, enumerable: true, configurable: true));

    internal static PropertyDescriptor ToDescriptor(JsValue value)
    {
        var obj = RequireObject(value);
        JsValue? Read(string key) => obj.HasProperty(key) ? obj.Get(key) : null;
        JsValue? ReadAccessor(string key)
        {
            var accessor = Read(key);
            if (accessor is { Type: not JsType.Undefined, Data: not JsFunction })
                throw JsException.TypeError("A property accessor must be callable or undefined");
            return accessor;
        }

        var descriptor = new PropertyDescriptor
        {
            Enumerable = Read("enumerable")?.ToBoolean(),
            Configurable = Read("configurable")?.ToBoolean(),
            Value = Read("value"),
            Writable = Read("writable")?.ToBoolean(),
            Get = ReadAccessor("get"),
            Set = ReadAccessor("set")
        };
        descriptor.Validate();
        return descriptor;
    }

    private static JsValue SetIntegrityLevel(JsValue value, bool freeze)
    {
        if (value.Data is not JsObject obj)
            return value;
        if (!obj.PreventExtensions())
            throw JsException.TypeError("Cannot prevent extensions");
        foreach (var key in obj.OwnPropertyKeys())
        {
            if (obj.GetOwnProperty(key) is { } descriptor)
                DefineOrThrow(obj, key, new PropertyDescriptor { Configurable = false, Writable = freeze && descriptor.IsDataDescriptor ? false : null });
        }
        return value;
    }

    private static bool TestIntegrityLevel(JsValue value, bool frozen)
    {
        if (value.Data is not JsObject obj)
            return true;
        if (obj.IsExtensible)
            return false;
        foreach (var key in obj.OwnPropertyKeys())
        {
            if (obj.GetOwnProperty(key) is { } descriptor && (descriptor.Configurable == true || frozen && descriptor.Writable == true))
                return false;
        }
        return true;
    }
}
