namespace ShoneJS.Runtime;

public sealed record PropertyDescriptor
{
    // Null means the corresponding field was omitted. JsValue.Undefined is an explicit field value.
    public JsValue? Value { get; init; }
    public bool? Writable { get; init; }
    public JsValue? Get { get; init; }
    public JsValue? Set { get; init; }
    public bool? Enumerable { get; init; }
    public bool? Configurable { get; init; }

    public bool IsDataDescriptor => Value.HasValue || Writable.HasValue;
    public bool IsAccessorDescriptor => Get.HasValue || Set.HasValue;

    public static PropertyDescriptor Data(JsValue value, bool writable = false, bool enumerable = false, bool configurable = false) =>
        new() { Value = value, Writable = writable, Enumerable = enumerable, Configurable = configurable };

    public static PropertyDescriptor Accessor(JsValue? get = null, JsValue? set = null, bool enumerable = false, bool configurable = false) =>
        new() { Get = get ?? JsValue.Undefined, Set = set ?? JsValue.Undefined, Enumerable = enumerable, Configurable = configurable };

    internal void Validate()
    {
        if (IsDataDescriptor && IsAccessorDescriptor)
            throw JsException.TypeError("A property descriptor cannot specify both data and accessor fields");

        ValidateAccessor(Get);
        ValidateAccessor(Set);
    }

    internal PropertyDescriptor Complete() => IsAccessorDescriptor
        ? Accessor(Get, Set, Enumerable ?? false, Configurable ?? false)
        : Data(Value ?? JsValue.Undefined, Writable ?? false, Enumerable ?? false, Configurable ?? false);

    internal PropertyDescriptor ApplyTo(PropertyDescriptor current)
    {
        var enumerable = Enumerable ?? current.Enumerable!.Value;
        var configurable = Configurable ?? current.Configurable!.Value;
        if (IsAccessorDescriptor || (!IsDataDescriptor && current.IsAccessorDescriptor))
            return Accessor(Get ?? current.Get, Set ?? current.Set, enumerable, configurable);

        return Data(Value ?? current.Value ?? JsValue.Undefined, Writable ?? current.Writable ?? false, enumerable, configurable);
    }

    internal bool IsCompatibleWith(PropertyDescriptor current)
    {
        if (current.Configurable == true)
            return true;
        if (Configurable == true || (Enumerable.HasValue && Enumerable != current.Enumerable))
            return false;
        if (IsAccessorDescriptor)
            return current.IsAccessorDescriptor && Matches(Get, current.Get) && Matches(Set, current.Set);
        if (IsDataDescriptor)
            return current.IsDataDescriptor && (current.Writable == true || (Writable != true && Matches(Value, current.Value)));
        return true;
    }

    private static void ValidateAccessor(JsValue? value)
    {
        if (value is { Type: not JsType.Undefined, Data: not JsFunction })
            throw JsException.TypeError("A property accessor must be callable or undefined");
    }

    private static bool Matches(JsValue? requested, JsValue? current) =>
        !requested.HasValue || (current.HasValue && AbstractOperations.SameValue(requested.Value, current.Value));
}
