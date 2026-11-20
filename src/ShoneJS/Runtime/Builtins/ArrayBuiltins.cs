// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;

namespace ShoneJS.Runtime.Builtins;

/// <summary>Installs Array and its prototype methods into an interpreter realm.</summary>
public static class ArrayBuiltins
{
    private const long MaxSafeLength = 9_007_199_254_740_991;

    [ThreadStatic]
    private static HashSet<JsObject>? _joining;

    public static JsArray Install(Action<string, JsValue> setGlobal, JsObject objectPrototype)
    {
        var realm = new ArrayRealm(objectPrototype);
        realm.Install(setGlobal);
        return realm.Prototype;
    }

    private sealed class ArrayRealm(JsObject objectPrototype)
    {
        public JsArray Prototype { get; } = new() { Prototype = objectPrototype };

        public void Install(Action<string, JsValue> setGlobal)
        {
            var constructor = new JsFunction(Construct, 1) { Prototype = objectPrototype, NativeConstruct = Construct };
            constructor.DefineOwnProperty("name", PropertyDescriptor.Data(JsValue.From("Array"), configurable: true));
            constructor.DefineOwnProperty("prototype", PropertyDescriptor.Data(JsValue.From(Prototype)));
            Prototype.DefineOwnProperty("constructor", PropertyDescriptor.Data(JsValue.From(constructor), writable: true, configurable: true));
            DefineMethod(constructor, "isArray", 1, (_, args) => JsValue.From(ProxyBuiltins.IsArray(Argument(args, 0).Data as JsObject)));

            DefineArrayMethod("concat", 1, Concat);
            DefineArrayMethod("every", 1, (obj, args) => Iterate(obj, args, Iteration.Every));
            DefineArrayMethod("filter", 1, (obj, args) => Iterate(obj, args, Iteration.Filter));
            DefineArrayMethod("find", 1, (obj, args) => Iterate(obj, args, Iteration.Find));
            DefineArrayMethod("findIndex", 1, (obj, args) => Iterate(obj, args, Iteration.FindIndex));
            DefineArrayMethod("findLast", 1, (obj, args) => Iterate(obj, args, Iteration.FindLast));
            DefineArrayMethod("findLastIndex", 1, (obj, args) => Iterate(obj, args, Iteration.FindLastIndex));
            DefineArrayMethod("forEach", 1, (obj, args) => Iterate(obj, args, Iteration.ForEach));
            DefineArrayMethod("includes", 1, (obj, args) => Search(obj, args, SearchKind.Includes));
            DefineArrayMethod("indexOf", 1, (obj, args) => Search(obj, args, SearchKind.IndexOf));
            DefineArrayMethod("join", 1, Join);
            DefineArrayMethod("lastIndexOf", 1, (obj, args) => Search(obj, args, SearchKind.LastIndexOf));
            DefineArrayMethod("map", 1, (obj, args) => Iterate(obj, args, Iteration.Map));
            DefineArrayMethod("pop", 0, Pop);
            DefineArrayMethod("push", 1, Push);
            DefineArrayMethod("reduce", 1, (obj, args) => Reduce(obj, args, right: false));
            DefineArrayMethod("reduceRight", 1, (obj, args) => Reduce(obj, args, right: true));
            DefineArrayMethod("reverse", 0, Reverse);
            DefineArrayMethod("shift", 0, Shift);
            DefineArrayMethod("slice", 2, Slice);
            DefineArrayMethod("some", 1, (obj, args) => Iterate(obj, args, Iteration.Some));
            DefineArrayMethod("splice", 2, Splice);
            DefineArrayMethod("toReversed", 0, ToReversed);
            DefineArrayMethod("toSorted", 1, ToSorted);
            DefineArrayMethod("toSpliced", 2, ToSpliced);
            DefineArrayMethod("with", 2, With);
            DefineMethod(Prototype, "toString", 0, ToString);
            DefineArrayMethod("unshift", 1, Unshift);
            setGlobal("Array", JsValue.From(constructor));
        }

        private void DefineMethod(JsObject target, string name, int arity, Func<JsValue, JsValue[], JsValue> call)
        {
            var method = new JsFunction(call, arity) { Prototype = objectPrototype };
            method.DefineOwnProperty("name", PropertyDescriptor.Data(JsValue.From(name), configurable: true));
            target.DefineOwnProperty(name, PropertyDescriptor.Data(JsValue.From(method), writable: true, configurable: true));
        }

        private void DefineArrayMethod(string name, int arity, Func<JsObject, JsValue[], JsValue> call) =>
            DefineMethod(Prototype, name, arity, (receiver, args) => call(ToObject(receiver), args));

        private JsValue Construct(JsValue[] args)
        {
            if (args.Length == 1 && args[0].Type == JsType.Number)
            {
                var length = args[0].ToNumber();
                if (length < 0 || length > uint.MaxValue || Math.Truncate(length) != length)
                    throw JsException.RangeError("Invalid array length");
                return JsValue.From(NewArray((long)length));
            }

            var result = NewArray();
            foreach (var value in args)
                result.Add(value);
            return JsValue.From(result);
        }

        private JsArray NewArray(long length = 0)
        {
            var result = new JsArray { Prototype = Prototype };
            if (length != 0)
                SetLength(result, length);
            return result;
        }

        // Result-producing methods share this constructor check until Symbol.species is modeled.
        private JsArray NewResultArray(JsObject source, long length = 0)
        {
            // Symbol.species is unavailable in the current property model. Still perform
            // the constructor lookup and reject a defined primitive constructor.
            if (ProxyBuiltins.IsArray(source))
            {
                var constructor = source.Get("constructor");
                if (constructor.Type != JsType.Undefined && constructor.Data is not JsObject)
                    throw JsException.TypeError("Invalid array constructor");
            }
            return NewArray(length);
        }

        private JsObject ToObject(JsValue value)
        {
            if (value.Data is JsObject obj)
                return obj;
            if (AbstractOperations.IsNullish(value))
                throw JsException.TypeError("Cannot convert a nullish value to an object");

            return new ReceiverWrapper(value) { Prototype = objectPrototype };
        }

        private JsValue ToString(JsValue receiver, JsValue[] _)
        {
            var obj = ToObject(receiver);
            if (obj.Get("join").Data is JsFunction join)
                return join.Call(JsValue.From(obj), []);

            var tag = obj switch
            {
                ReceiverWrapper wrapper => wrapper.Value.Type.ToString(),
                JsFunction => "Function",
                JsError => "Error",
                _ => ProxyBuiltins.IsArray(obj) ? "Array" : "Object"
            };
            return JsValue.From($"[object {tag}]");
        }

        private JsValue Concat(JsObject obj, JsValue[] args)
        {
            var result = NewResultArray(obj);
            long next = 0;
            void Append(JsValue item)
            {
                // Symbol.isConcatSpreadable is unavailable in the current property model.
                if (item.Data is JsObject candidate && ProxyBuiltins.IsArray(candidate))
                {
                    var length = LengthOf(candidate);
                    CheckSafeLength(next + length);
                    for (long index = 0; index < length; index++, next++)
                    {
                        var key = Key(index);
                        if (candidate.HasProperty(key))
                            CreateElement(result, Key(next), candidate.Get(key));
                    }
                }
                else
                {
                    CheckSafeLength(next + 1);
                    CreateElement(result, Key(next++), item);
                }
            }

            Append(JsValue.From(obj));
            foreach (var item in args)
                Append(item);
            SetLength(result, next);
            return JsValue.From(result);
        }

        private JsValue Slice(JsObject obj, JsValue[] args)
        {
            var length = LengthOf(obj);
            var start = RelativeIndex(Argument(args, 0), length);
            var endArg = Argument(args, 1);
            var end = endArg.Type == JsType.Undefined ? length : RelativeIndex(endArg, length);
            var result = NewResultArray(obj, Math.Max(end - start, 0));
            CopyPresent(obj, start, end, result);
            return JsValue.From(result);
        }

        private JsValue ToReversed(JsObject obj, JsValue[] _)
        {
            var length = LengthOf(obj);
            var result = NewArray(length);
            for (long index = 0; index < length; index++)
                CreateElement(result, Key(index), obj.Get(Key(length - index - 1)));
            return JsValue.From(result);
        }

        private JsValue ToSorted(JsObject obj, JsValue[] args)
        {
            var length = LengthOf(obj);
            var comparator = Argument(args, 0);
            if (comparator.Type != JsType.Undefined && comparator.Data is not JsFunction)
                throw JsException.TypeError("Comparator must be callable");

            var values = new List<JsValue>((int)Math.Min(length, int.MaxValue));
            for (long index = 0; index < length; index++)
                values.Add(obj.Get(Key(index)));
            var decorated = values.Select((value, index) => (Value: value, Index: index, Text: (string?)null)).ToArray();
            if (comparator.Type == JsType.Undefined)
            {
                for (var index = 0; index < decorated.Length; index++)
                    decorated[index].Text = AbstractOperations.IsNullish(decorated[index].Value) ? null : ToStringValue(decorated[index].Value);
            }
            Array.Sort(decorated, (left, right) =>
            {
                var leftNull = AbstractOperations.IsNullish(left.Value);
                var rightNull = AbstractOperations.IsNullish(right.Value);
                int order;
                if (leftNull || rightNull)
                    order = leftNull == rightNull ? 0 : leftNull ? 1 : -1;
                else if (comparator.Data is JsFunction compare)
                {
                    var number = compare.Call(JsValue.Undefined, [left.Value, right.Value]).ToNumber();
                    order = double.IsNaN(number) || number == 0 ? 0 : number < 0 ? -1 : 1;
                }
                else
                    order = string.CompareOrdinal(left.Text, right.Text);
                return order != 0 ? order : left.Index.CompareTo(right.Index);
            });
            var result = NewArray(length);
            for (var index = 0; index < decorated.Length; index++)
                CreateElement(result, Key(index), decorated[index].Value);
            return JsValue.From(result);
        }

        private JsValue ToSpliced(JsObject obj, JsValue[] args)
        {
            var length = LengthOf(obj);
            var start = RelativeIndex(Argument(args, 0), length);
            var deleteCount = args.Length switch
            {
                0 => 0,
                1 => length - start,
                _ => (long)Math.Clamp(ToInteger(args[1]), 0, length - start)
            };
            var itemCount = Math.Max(args.Length - 2, 0);
            var newLength = length - deleteCount + itemCount;
            CheckSafeLength(newLength);
            var result = NewArray(newLength);
            long target = 0;
            for (long index = 0; index < start; index++)
                CreateElement(result, Key(target++), obj.Get(Key(index)));
            for (var index = 0; index < itemCount; index++)
                CreateElement(result, Key(target++), args[index + 2]);
            for (var index = start + deleteCount; index < length; index++)
                CreateElement(result, Key(target++), obj.Get(Key(index)));
            return JsValue.From(result);
        }

        private JsValue With(JsObject obj, JsValue[] args)
        {
            var length = LengthOf(obj);
            var index = ToInteger(Argument(args, 0));
            var actualIndex = index < 0 ? length + index : index;
            if (actualIndex < 0 || actualIndex >= length)
                throw JsException.RangeError("Invalid index");
            var replacement = Argument(args, 1);
            var result = NewArray(length);
            for (long position = 0; position < length; position++)
                CreateElement(result, Key(position), position == actualIndex ? replacement : obj.Get(Key(position)));
            return JsValue.From(result);
        }

        private JsValue Splice(JsObject obj, JsValue[] args)
        {
            var length = LengthOf(obj);
            var start = RelativeIndex(Argument(args, 0), length);
            var deleteCount = args.Length switch
            {
                0 => 0,
                1 => length - start,
                _ => (long)Math.Clamp(ToInteger(args[1]), 0, length - start)
            };
            var itemCount = Math.Max(args.Length - 2, 0);
            var newLength = length - deleteCount + itemCount;
            CheckSafeLength(newLength);

            var removed = NewResultArray(obj, deleteCount);
            CopyPresent(obj, start, start + deleteCount, removed);
            if (itemCount < deleteCount)
            {
                for (var index = start; index < length - deleteCount; index++)
                    MoveProperty(obj, index + deleteCount, index + itemCount);
                for (var index = length; index > newLength; index--)
                    DeleteProperty(obj, Key(index - 1));
            }
            else if (itemCount > deleteCount)
            {
                for (var index = length - deleteCount; index > start; index--)
                    MoveProperty(obj, index + deleteCount - 1, index + itemCount - 1);
            }
            for (var index = 0; index < itemCount; index++)
                SetProperty(obj, Key(start + index), args[index + 2]);
            SetLength(obj, newLength);
            return JsValue.From(removed);
        }

        private enum Iteration { Every, Filter, Find, FindIndex, FindLast, FindLastIndex, ForEach, Map, Some }

        private JsValue Iterate(JsObject obj, JsValue[] args, Iteration operation)
        {
            var length = LengthOf(obj);
            var callback = RequireCallback(args);
            var thisArg = Argument(args, 1);
            var receiver = JsValue.From(obj);
            // Find variants call the predicate for holes as undefined; map/filter and tests skip holes.
            var visitHoles = operation is Iteration.Find or Iteration.FindIndex or Iteration.FindLast or Iteration.FindLastIndex;
            var result = operation switch
            {
                Iteration.Map => NewResultArray(obj, length),
                Iteration.Filter => NewResultArray(obj),
                _ => null
            };
            long next = 0;

            for (long step = 0; step < length; step++)
            {
                var index = operation is Iteration.FindLast or Iteration.FindLastIndex ? length - step - 1 : step;
                var key = Key(index);
                if (!visitHoles && !obj.HasProperty(key))
                    continue;

                var value = obj.Get(key);
                var indexValue = Number(index);
                var selected = callback.Call(thisArg, [value, indexValue, receiver]);
                switch (operation)
                {
                    case Iteration.Every when !selected.ToBoolean(): return JsValue.From(false);
                    case Iteration.Some when selected.ToBoolean(): return JsValue.From(true);
                    case Iteration.Find when selected.ToBoolean(): return value;
                    case Iteration.FindIndex when selected.ToBoolean(): return indexValue;
                    case Iteration.FindLast when selected.ToBoolean(): return value;
                    case Iteration.FindLastIndex when selected.ToBoolean(): return indexValue;
                    case Iteration.Filter when selected.ToBoolean(): CreateElement(result!, Key(next++), value); break;
                    case Iteration.Map: CreateElement(result!, key, selected); break;
                }
            }

            return operation switch
            {
                Iteration.Every => JsValue.From(true),
                Iteration.Some => JsValue.From(false),
                Iteration.FindIndex or Iteration.FindLastIndex => Number(-1),
                Iteration.FindLast => JsValue.Undefined,
                Iteration.Map or Iteration.Filter => JsValue.From(result!),
                _ => JsValue.Undefined
            };
        }
    }

    private sealed class ReceiverWrapper(JsValue value) : JsObject
    {
        public JsValue Value { get; } = value;

        public override PropertyDescriptor? GetOwnProperty(string key)
        {
            if (Value.Data is string text)
            {
                if (key == "length")
                    return PropertyDescriptor.Data(Number(text.Length));
                if (TryArrayIndex(key, out var index) && index < text.Length)
                    return PropertyDescriptor.Data(JsValue.From(text[(int)index].ToString()), enumerable: true);
            }
            return base.GetOwnProperty(key);
        }

        public override bool DefineOwnProperty(string key, PropertyDescriptor descriptor)
        {
            if (Value.Data is string text && (key == "length" || (TryArrayIndex(key, out var index) && index < text.Length)))
                return descriptor.IsCompatibleWith(GetOwnProperty(key)!);
            return base.DefineOwnProperty(key, descriptor);
        }

        public override bool Delete(string key) => GetOwnProperty(key)?.Configurable != false && base.Delete(key);

        public override IEnumerable<string> OwnPropertyKeys()
        {
            if (Value.Data is not string text)
                return base.OwnPropertyKeys();
            return Enumerable.Range(0, text.Length).Select(index => Key(index))
                .Concat(base.OwnPropertyKeys().TakeWhile(key => TryArrayIndex(key, out _)))
                .Append("length")
                .Concat(base.OwnPropertyKeys().SkipWhile(key => TryArrayIndex(key, out _)));
        }
    }

    private static JsValue Join(JsObject obj, JsValue[] args)
    {
        var length = LengthOf(obj);
        var argument = Argument(args, 0);
        var separator = argument.Type == JsType.Undefined ? "," : ToStringValue(argument);
        var joining = _joining ??= new HashSet<JsObject>(ReferenceEqualityComparer.Instance);
        if (!joining.Add(obj))
            return JsValue.From("");
        try
        {
            var builder = new StringBuilder();
            for (long index = 0; index < length; index++)
            {
                if (index > 0)
                    builder.Append(separator);
                var value = obj.Get(Key(index));
                if (!AbstractOperations.IsNullish(value))
                    builder.Append(ToStringValue(value));
            }
            return JsValue.From(builder.ToString());
        }
        finally
        {
            joining.Remove(obj);
        }
    }

    private static JsValue Pop(JsObject obj, JsValue[] _)
    {
        var length = LengthOf(obj);
        if (length == 0)
        {
            SetLength(obj, 0);
            return JsValue.Undefined;
        }
        var key = Key(length - 1);
        var result = obj.Get(key);
        DeleteProperty(obj, key);
        SetLength(obj, length - 1);
        return result;
    }

    private static JsValue Push(JsObject obj, JsValue[] args)
    {
        var length = LengthOf(obj);
        CheckSafeLength(length + args.Length);
        foreach (var value in args)
            SetProperty(obj, Key(length++), value);
        SetLength(obj, length);
        return Number(length);
    }

    private static JsValue Shift(JsObject obj, JsValue[] _)
    {
        var length = LengthOf(obj);
        if (length == 0)
        {
            SetLength(obj, 0);
            return JsValue.Undefined;
        }
        var first = obj.Get("0");
        for (long index = 1; index < length; index++)
            MoveProperty(obj, index, index - 1);
        DeleteProperty(obj, Key(length - 1));
        SetLength(obj, length - 1);
        return first;
    }

    private static JsValue Unshift(JsObject obj, JsValue[] args)
    {
        var length = LengthOf(obj);
        var newLength = length + args.Length;
        CheckSafeLength(newLength);
        if (args.Length != 0)
        {
            for (var index = length; index > 0; index--)
                MoveProperty(obj, index - 1, index + args.Length - 1);
            for (var index = 0; index < args.Length; index++)
                SetProperty(obj, Key(index), args[index]);
        }
        SetLength(obj, newLength);
        return Number(newLength);
    }

    private static JsValue Reverse(JsObject obj, JsValue[] _)
    {
        var length = LengthOf(obj);
        for (long lower = 0; lower < length / 2; lower++)
        {
            var lowerKey = Key(lower);
            var upperKey = Key(length - lower - 1);
            var hasLower = obj.HasProperty(lowerKey);
            var lowerValue = hasLower ? obj.Get(lowerKey) : JsValue.Undefined;
            var hasUpper = obj.HasProperty(upperKey);
            var upperValue = hasUpper ? obj.Get(upperKey) : JsValue.Undefined;

            if (hasLower && hasUpper)
            {
                SetProperty(obj, lowerKey, upperValue);
                SetProperty(obj, upperKey, lowerValue);
            }
            else if (hasUpper)
            {
                SetProperty(obj, lowerKey, upperValue);
                DeleteProperty(obj, upperKey);
            }
            else if (hasLower)
            {
                DeleteProperty(obj, lowerKey);
                SetProperty(obj, upperKey, lowerValue);
            }
        }
        return JsValue.From(obj);
    }

    private enum SearchKind { Includes, IndexOf, LastIndexOf }

    private static JsValue Search(JsObject obj, JsValue[] args, SearchKind kind)
    {
        var length = LengthOf(obj);
        var includes = kind == SearchKind.Includes;
        var missing = includes ? JsValue.From(false) : Number(-1);
        if (length == 0)
            return missing;

        var backwards = kind == SearchKind.LastIndexOf;
        var search = Argument(args, 0);
        var from = backwards && args.Length < 2 ? length - 1 : ToInteger(Argument(args, 1));
        var start = from < 0 ? length + from : from;
        start = backwards ? Math.Min(start, length - 1) : Math.Max(start, 0);
        if (start < 0 || start >= length)
            return missing;

        for (var index = (long)start; index >= 0 && index < length; index += backwards ? -1 : 1)
        {
            var key = Key(index);
            if (!includes && !obj.HasProperty(key))
                continue;
            var value = obj.Get(key);
            if (AbstractOperations.StrictEqual(search, value) ||
                (includes && search.Type == JsType.Number && value.Type == JsType.Number &&
                 double.IsNaN(search.ToNumber()) && double.IsNaN(value.ToNumber())))
                return includes ? JsValue.From(true) : Number(index);
        }
        return missing;
    }

    private static JsValue Reduce(JsObject obj, JsValue[] args, bool right)
    {
        var length = LengthOf(obj);
        var callback = RequireCallback(args);
        var hasAccumulator = args.Length > 1;
        var accumulator = Argument(args, 1);
        var receiver = JsValue.From(obj);
        for (var index = right ? length - 1 : 0; index >= 0 && index < length; index += right ? -1 : 1)
        {
            var key = Key(index);
            if (!obj.HasProperty(key))
                continue;
            var value = obj.Get(key);
            accumulator = hasAccumulator ? callback.Call(JsValue.Undefined, [accumulator, value, Number(index), receiver]) : value;
            hasAccumulator = true;
        }
        if (!hasAccumulator)
            throw JsException.TypeError("Reduce of an empty array with no initial value");
        return accumulator;
    }

    private static void CopyPresent(JsObject source, long start, long end, JsArray target)
    {
        for (var index = start; index < end; index++)
        {
            var key = Key(index);
            if (source.HasProperty(key))
                CreateElement(target, Key(index - start), source.Get(key));
        }
    }

    private static void MoveProperty(JsObject obj, long from, long to)
    {
        var fromKey = Key(from);
        var toKey = Key(to);
        if (obj.HasProperty(fromKey))
            SetProperty(obj, toKey, obj.Get(fromKey));
        else
            DeleteProperty(obj, toKey);
    }

    private static JsFunction RequireCallback(JsValue[] args) =>
        Argument(args, 0).Data as JsFunction ?? throw JsException.TypeError("Array callback must be callable");

    private static long LengthOf(JsObject obj) => (long)Math.Clamp(ToInteger(obj.Get("length")), 0, MaxSafeLength);

    private static double ToInteger(JsValue value)
    {
        var number = AbstractOperations.ToPrimitive(value).ToNumber();
        return double.IsNaN(number) ? 0 : Math.Truncate(number);
    }

    private static long RelativeIndex(JsValue value, long length)
    {
        var index = ToInteger(value);
        return (long)(index < 0 ? Math.Max(length + index, 0) : Math.Min(index, length));
    }

    private static string ToStringValue(JsValue value) => AbstractOperations.ToPrimitive(value, preferString: true).ToJsString();

    private static void CheckSafeLength(long length)
    {
        if (length > MaxSafeLength)
            throw JsException.TypeError("Array-like length exceeds the maximum safe integer");
    }

    private static void SetLength(JsObject obj, long length) => SetProperty(obj, "length", Number(length));

    private static void SetProperty(JsObject obj, string key, JsValue value)
    {
        if (!obj.Set(key, value, JsValue.From(obj)))
            throw JsException.TypeError($"Cannot set property '{key}'");
    }

    private static void DeleteProperty(JsObject obj, string key)
    {
        if (!obj.Delete(key))
            throw JsException.TypeError($"Cannot delete property '{key}'");
    }

    private static void CreateElement(JsArray array, string key, JsValue value)
    {
        if (!array.DefineOwnProperty(key, PropertyDescriptor.Data(value, writable: true, enumerable: true, configurable: true)))
            throw JsException.TypeError($"Cannot define array element '{key}'");
    }

    private static JsValue Argument(JsValue[] args, int index) => index < args.Length ? args[index] : JsValue.Undefined;
    private static JsValue Number(long value) => JsValue.From((double)value);
    private static string Key(long index) => index.ToString(CultureInfo.InvariantCulture);
}
