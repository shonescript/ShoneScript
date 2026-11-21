// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Globalization;

namespace ShoneJs.Runtime;

/// <summary>Ordinary JavaScript object storage and internal property operations.</summary>
public class JsObject
{
    private readonly OrderedDictionary<string, PropertyDescriptor> _properties = new(StringComparer.Ordinal);
    private JsObject? _prototype;
    private bool _isExtensible = true;

    public JsObject? Prototype
    {
        get => GetPrototypeOf();
        set
        {
            if (!SetPrototypeOf(value))
                throw JsException.TypeError("Cannot change the object's prototype");
        }
    }

    public virtual string? HostObjectTag => null;

    public virtual JsObject? GetPrototypeOf() => _prototype;
    internal virtual bool HasOrdinaryPrototypeLookup => true;

    public virtual bool SetPrototypeOf(JsObject? prototype)
    {
        if (ReferenceEquals(prototype, _prototype))
            return true;
        if (!IsExtensible)
            return false;

        var visited = new HashSet<JsObject>(ReferenceEqualityComparer.Instance) { this };
        for (var current = prototype; current is not null; current = current.GetPrototypeOf())
        {
            if (!visited.Add(current))
                return false;
            if (!current.HasOrdinaryPrototypeLookup) break;
        }

        _prototype = prototype;
        return true;
    }

    public virtual bool IsExtensible => _isExtensible;

    public virtual bool PreventExtensions()
    {
        _isExtensible = false;
        return true;
    }

    public virtual PropertyDescriptor? GetOwnProperty(string key) => _properties.GetValueOrDefault(key);

    /// <summary>Defines or updates an own property, returning false when its descriptor is incompatible.</summary>
    public virtual bool DefineOwnProperty(string key, PropertyDescriptor descriptor)
    {
        descriptor.Validate();
        if (_properties.TryGetValue(key, out var current))
        {
            if (!descriptor.IsCompatibleWith(current))
                return false;
            _properties[key] = descriptor.ApplyTo(current);
        }
        else
        {
            if (!IsExtensible)
                return false;
            _properties.Add(key, descriptor.Complete());
        }
        return true;
    }

    public virtual bool Delete(string key)
    {
        if (!_properties.TryGetValue(key, out var descriptor))
            return true;
        return descriptor.Configurable == true && _properties.Remove(key);
    }

    /// <summary>Returns own string keys with array indices first in ascending numeric order.</summary>
    public virtual IEnumerable<string> OwnPropertyKeys()
    {
        var count = _properties.Count;
        if (count == 0) return Array.Empty<string>();
        var keys = new string[count];
        _properties.Keys.CopyTo(keys, 0);
        if (count == 1) return keys;

        // Keep the copied insertion order when it already matches JavaScript key order.
        var seenName = false;
        uint previousIndex = 0;
        foreach (var key in keys)
        {
            if (!TryArrayIndex(key, out var index))
                seenName = true;
            else if (seenName || index < previousIndex)
            {
                OrderArrayIndices(keys);
                break;
            }
            else
                previousIndex = index;
        }
        return keys;
    }

    private static void OrderArrayIndices(string[] keys)
    {
        var indices = new List<(uint Index, string Key)>();
        var nameCount = 0;
        var needsSort = false;
        foreach (var key in keys)
        {
            if (TryArrayIndex(key, out var index))
            {
                if (indices.Count != 0 && index < indices[^1].Index) needsSort = true;
                indices.Add((index, key));
            }
            else keys[nameCount++] = key;
        }

        // The remaining names retain their insertion order. Only the numeric keys may need sorting.
        Array.Copy(keys, 0, keys, indices.Count, nameCount);
        if (needsSort) indices.Sort(static (left, right) => left.Index.CompareTo(right.Index));
        for (var i = 0; i < indices.Count; i++) keys[i] = indices[i].Key;
    }

    public bool HasOwn(string key) => GetOwnProperty(key) is not null;

    public virtual bool HasProperty(string key) => HasOwn(key) || GetPrototypeOf()?.HasProperty(key) == true;

    public virtual JsValue Get(string key) => Get(key, JsValue.From(this));

    /// <summary>Reads through the prototype chain while preserving the original receiver for accessors.</summary>
    public virtual JsValue Get(string key, JsValue receiver)
    {
        var descriptor = GetOwnProperty(key);
        if (descriptor is null)
            return GetPrototypeOf()?.Get(key, receiver) ?? JsValue.Undefined;
        if (descriptor.IsDataDescriptor)
            return descriptor.Value!.Value;
        return descriptor.Get?.Data is JsFunction getter ? getter.Call(receiver, []) : JsValue.Undefined;
    }

    public virtual void Set(string key, JsValue value) => Set(key, value, JsValue.From(this));

    /// <summary>Writes using the supplied receiver, which may differ from the object holding the property.</summary>
    public virtual bool Set(string key, JsValue value, JsValue receiver)
    {
        var descriptor = GetOwnProperty(key);
        if (descriptor is null && GetPrototypeOf() is { } prototype)
            return prototype.Set(key, value, receiver);

        if (descriptor?.IsAccessorDescriptor == true)
        {
            if (descriptor.Set?.Data is not JsFunction setter)
                return false;
            setter.Call(receiver, [value]);
            return true;
        }

        if (descriptor?.Writable == false || receiver.Data is not JsObject target)
            return false;

        var existing = target.GetOwnProperty(key);
        if (existing is null)
            return target.DefineOwnProperty(key, PropertyDescriptor.Data(value, writable: true, enumerable: true, configurable: true));
        if (existing.IsAccessorDescriptor || existing.Writable != true)
            return false;
        return target.DefineOwnProperty(key, new PropertyDescriptor { Value = value });
    }

    public IEnumerable<KeyValuePair<string, JsValue>> Properties
    {
        get
        {
            foreach (var key in OwnPropertyKeys())
            {
                if (GetOwnProperty(key)?.Enumerable == true)
                    yield return new KeyValuePair<string, JsValue>(key, Get(key));
            }
        }
    }

    internal static bool TryArrayIndex(string key, out uint index)
    {
        index = 0;
        if (key.Length is 0 or > 10 || (key.Length > 1 && key[0] == '0'))
            return false;
        ulong number = 0;
        foreach (var character in key)
        {
            if (character is < '0' or > '9')
                return false;
            number = number * 10 + (uint)(character - '0');
        }
        if (number > uint.MaxValue) return false;
        index = (uint)number;
        return index < uint.MaxValue;
    }
}

/// <summary>An array-like object with indexed properties and a descriptor-controlled length.</summary>
public sealed class JsArray : JsObject
{
    public JsArray() => base.DefineOwnProperty("length", PropertyDescriptor.Data(JsValue.From(0d), writable: true));

    public int Length => checked((int)ArrayLength);
    public uint ArrayLength => (uint)GetOwnProperty("length")!.Value!.Value.ToNumber();

    public void Add(JsValue value)
    {
        var length = ArrayLength;
        if (length == uint.MaxValue)
            throw JsException.RangeError("Invalid array length");
        if (!DefineOwnProperty(length.ToString(CultureInfo.InvariantCulture), PropertyDescriptor.Data(value, writable: true, enumerable: true, configurable: true)))
            throw JsException.TypeError("Cannot add an array element");
    }

    public override bool DefineOwnProperty(string key, PropertyDescriptor descriptor)
    {
        if (key == "length")
            return DefineLength(descriptor);
        if (!TryArrayIndex(key, out var index))
            return base.DefineOwnProperty(key, descriptor);

        descriptor.Validate();
        var lengthDescriptor = GetOwnProperty("length")!;
        var length = (uint)lengthDescriptor.Value!.Value.ToNumber();
        if (index >= length && lengthDescriptor.Writable == false)
            return false;
        if (!base.DefineOwnProperty(key, descriptor))
            return false;
        if (index >= length)
            base.DefineOwnProperty("length", new PropertyDescriptor { Value = JsValue.From(index + 1d) });
        return true;
    }

    private bool DefineLength(PropertyDescriptor descriptor)
    {
        descriptor.Validate();
        if (descriptor.Value is not { } value)
            return base.DefineOwnProperty("length", descriptor);

        var number = value.ToNumber();
        if (!double.IsFinite(number) || number < 0 || number > uint.MaxValue || Math.Truncate(number) != number)
            throw JsException.RangeError("Invalid array length");

        var length = (uint)number;
        descriptor = descriptor with { Value = JsValue.From((double)length) };
        if (length >= ArrayLength)
            return base.DefineOwnProperty("length", descriptor);

        // Leave length writable until the index deletions finish, so it can be restored if a deletion fails.
        var makeReadOnly = descriptor.Writable == false;
        var pending = makeReadOnly ? descriptor with { Writable = true } : descriptor;
        if (!base.DefineOwnProperty("length", pending))
            return false;

        var indicesToDelete = OwnPropertyKeys()
            .Select(key => (Key: key, Index: TryArrayIndex(key, out var index) ? (uint?)index : null))
            .Where(entry => entry.Index >= length)
            .OrderByDescending(entry => entry.Index);
        foreach (var entry in indicesToDelete)
        {
            if (Delete(entry.Key))
                continue;
            base.DefineOwnProperty("length", new PropertyDescriptor
            {
                Value = JsValue.From(entry.Index!.Value + 1d),
                Writable = makeReadOnly ? false : null
            });
            return false;
        }

        if (makeReadOnly)
            base.DefineOwnProperty("length", new PropertyDescriptor { Writable = false });
        return true;
    }
}

/// <summary>A callable object with optional construction behavior.</summary>
public sealed class JsFunction : JsObject
{
    private readonly Func<JsValue, JsValue[], JsValue> _call;

    public JsFunction(Func<JsValue[], JsValue> call, int arity = 0)
        : this((_, args) => call(args), arity) { }

    public JsFunction(Func<JsValue, JsValue[], JsValue> call, int arity = 0)
    {
        _call = call;
        Arity = arity;
        DefineOwnProperty("length", PropertyDescriptor.Data(JsValue.From((double)arity), configurable: true));
    }

    public JsFunction(Func<JsValue, JsValue[], JsValue> call, int arity, Func<JsValue[], JsValue> construct)
        : this(call, arity)
    {
        ArgumentNullException.ThrowIfNull(construct);
        NativeConstruct = construct;
    }

    public int Arity { get; }
    internal Func<JsValue[], JsValue>? NativeConstruct { get; init; }
    internal JsObject? DefaultConstructionPrototype { get; init; }
    internal bool IsConstructor => NativeConstruct is not null || DefaultConstructionPrototype is not null;

    public JsValue Call(params JsValue[] args) => Call(JsValue.Undefined, args);

    public JsValue Call(JsValue receiver, JsValue[] args) => _call(receiver, args);

    // Native constructors currently support only themselves as newTarget.
    internal JsValue Construct(JsValue[] args, JsFunction? newTarget = null)
    {
        newTarget ??= this;
        if (!IsConstructor || !newTarget.IsConstructor)
            throw JsException.TypeError("Value is not a constructor");
        if (NativeConstruct is { } construct)
        {
            if (ReferenceEquals(newTarget, this)) return construct(args);
            throw new NotSupportedException("Alternate newTarget for native constructors is not implemented");
        }
        var prototype = newTarget.Get("prototype").Data as JsObject ?? newTarget.DefaultConstructionPrototype ?? DefaultConstructionPrototype;
        var receiver = JsValue.From(new JsObject { Prototype = prototype });
        var result = Call(receiver, args);
        return result.Data is JsObject ? result : receiver;
    }
}
