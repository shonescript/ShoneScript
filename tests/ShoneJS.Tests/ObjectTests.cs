using ShoneJS.Runtime;

namespace ShoneJS.Tests;

public class ObjectTests
{
    [Fact]
    public void InheritedAccessorsUseTheReceiver()
    {
        var prototype = new JsObject();
        var child = new JsObject { Prototype = prototype };
        var getter = new JsFunction((receiver, _) => ((JsObject)receiver.Data!).Get("backing"));
        var setter = new JsFunction((receiver, args) =>
        {
            ((JsObject)receiver.Data!).Set("backing", args[0]);
            return JsValue.Undefined;
        });
        Assert.True(prototype.DefineOwnProperty("x", PropertyDescriptor.Accessor(JsValue.From(getter), JsValue.From(setter))));

        Assert.True(child.Set("x", JsValue.From(37d), JsValue.From(child)));
        Assert.Equal(37d, child.Get("x").ToNumber());
        Assert.True(child.HasProperty("x"));
        Assert.False(child.HasOwn("x"));
        Assert.False(prototype.HasOwn("backing"));
    }

    [Fact]
    public void InheritedDataWritesRespectTheDescriptorAndReceiver()
    {
        var prototype = new JsObject();
        prototype.Set("writable", JsValue.From(1d));
        Assert.True(prototype.DefineOwnProperty("fixed", PropertyDescriptor.Data(JsValue.From(2d))));
        var child = new JsObject { Prototype = prototype };
        var receiver = JsValue.From(child);

        Assert.True(child.Set("writable", JsValue.From(3d), receiver));
        Assert.False(child.Set("fixed", JsValue.From(4d), receiver));
        Assert.False(child.Set("writable", JsValue.From(5d), JsValue.From(6d)));
        Assert.Equal(3d, child.Get("writable").ToNumber());
        Assert.Equal(1d, prototype.Get("writable").ToNumber());
        Assert.False(child.HasOwn("fixed"));
        Assert.Equal(2d, child.Get("fixed").ToNumber());
    }

    [Fact]
    public void NonConfigurableDescriptorsEnforceSameValueAndPreserveOmittedFields()
    {
        var obj = new JsObject();
        Assert.True(obj.DefineOwnProperty("nan", PropertyDescriptor.Data(JsValue.From(double.NaN), enumerable: true)));
        Assert.True(obj.DefineOwnProperty("zero", PropertyDescriptor.Data(JsValue.From(0d))));
        Assert.True(obj.DefineOwnProperty("nan", new PropertyDescriptor { Value = JsValue.From(double.NaN) }));
        Assert.False(obj.DefineOwnProperty("zero", new PropertyDescriptor { Value = JsValue.From(-0d) }));
        Assert.False(obj.DefineOwnProperty("nan", new PropertyDescriptor { Enumerable = false }));
        Assert.False(obj.DefineOwnProperty("nan", PropertyDescriptor.Accessor()));
        Assert.False(obj.Delete("nan"));
        Assert.True(obj.GetOwnProperty("nan")!.Enumerable);
    }

    [Fact]
    public void PrototypeChangesRejectCyclesAndRespectExtensibility()
    {
        var root = new JsObject();
        var child = new JsObject { Prototype = root };
        Assert.False(root.SetPrototypeOf(child));
        Assert.False(child.SetPrototypeOf(child));
        Assert.Null(root.Prototype);
        Assert.True(child.PreventExtensions());
        Assert.True(child.SetPrototypeOf(root));
        Assert.False(child.SetPrototypeOf(null));
        Assert.False(child.DefineOwnProperty("x", PropertyDescriptor.Data(JsValue.Undefined)));
    }

    [Fact]
    public void SparseArraysUseCanonicalIndicesAndExposeHolesToPrototypeLookup()
    {
        var prototype = new JsObject();
        prototype.Set("1", JsValue.From(9d));
        var array = new JsArray { Prototype = prototype };
        array.Set("4294967294", JsValue.From(7d));
        array.Set("4294967295", JsValue.From(6d));
        array.Set("01", JsValue.From(4d));
        array.Set("+1", JsValue.From(5d));
        array.Set(" 1", JsValue.From(8d));

        Assert.Equal(uint.MaxValue, array.ArrayLength);
        Assert.Equal((double)uint.MaxValue, array.Get("length").ToNumber());
        Assert.False(array.HasOwn("1"));
        Assert.Equal(9d, array.Get("1").ToNumber());
        Assert.Equal(new[] { "4294967294", "length", "4294967295", "01", "+1", " 1" }, array.OwnPropertyKeys());
        array.Set("length", JsValue.From(2d));
        Assert.Equal(2, array.Length);
        Assert.False(array.HasOwn("4294967294"));
        Assert.True(array.HasOwn("4294967295"));
        Assert.False(array.HasOwn("0"));
    }

    [Fact]
    public void ShorteningAnArrayStopsAtNonConfigurableIndices()
    {
        var array = new JsArray();
        Assert.True(array.DefineOwnProperty("2", PropertyDescriptor.Data(JsValue.From(2d))));
        array.Set("3", JsValue.From(3d));
        Assert.False(array.DefineOwnProperty("length", new PropertyDescriptor { Value = JsValue.From(1d), Writable = false }));

        Assert.Equal(3, array.Length);
        Assert.True(array.HasOwn("2"));
        Assert.False(array.HasOwn("3"));
        Assert.False(array.Set("3", JsValue.From(3d), JsValue.From(array)));
        Assert.False(array.GetOwnProperty("length")!.Writable);
    }

    [Theory]
    [InlineData(-1d)]
    [InlineData(1.5d)]
    [InlineData(4294967296d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidArrayLengthThrowsRangeError(double value)
    {
        var array = new JsArray();
        var error = Assert.Throws<JsException>(() => array.Set("length", JsValue.From(value)));
        Assert.Equal("RangeError", error.ErrorName);
        Assert.Equal(0, array.Length);
    }

    [Fact]
    public void FunctionsSupportPropertiesAndReceiverAwareCalls()
    {
        var receiver = new JsObject();
        var legacy = new JsFunction(args => args[0], 1);
        var function = new JsFunction((thisValue, _) => thisValue);
        function.Set("extra", JsValue.From(true));

        Assert.True(function.Get("extra").ToBoolean());
        Assert.Same(receiver, function.Call(JsValue.From(receiver), []).Data);
        Assert.Equal(JsType.Undefined, function.Call().Type);
        Assert.Equal(1d, legacy.Get("length").ToNumber());
        Assert.Equal(5d, legacy.Call(JsValue.From(5d)).ToNumber());
    }
}
