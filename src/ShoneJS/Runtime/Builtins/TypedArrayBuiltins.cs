// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Globalization;
using Sdcb.Arithmetic;

namespace ShoneScript.Runtime.Builtins;

public static class TypedArrayBuiltins
{
    /// <summary>Fixed-size byte storage shared by typed-array views.</summary>
    public sealed class BufferObject : JsObject
    {
        internal BufferObject(int size) => Bytes = new byte[size];
        internal byte[] Bytes { get; }
        public int ByteLength => Bytes.Length;
    }

    /// <summary>A fixed-length typed view; subarrays share the underlying buffer.</summary>
    public sealed class ViewObject : JsObject
    {
        internal ViewObject(BufferObject buffer, int length, Kind kind, JsObject? prototype, int byteOffset = 0)
        {
            Buffer = buffer; Length = length; ElementKind = kind; ByteOffset = byteOffset; Prototype = prototype;
        }
        public BufferObject Buffer { get; }
        public int Length { get; }
        public int ByteOffset { get; }
        public Kind ElementKind { get; }
        public int BytesPerElement => Size(ElementKind);
        public override PropertyDescriptor? GetOwnProperty(string key)
        {
            if (key == "length") return PropertyDescriptor.Data(JsValue.From((double)Length));
            if (key == "byteLength") return PropertyDescriptor.Data(JsValue.From((double)(Length * BytesPerElement)));
            if (key == "byteOffset") return PropertyDescriptor.Data(JsValue.From((double)ByteOffset));
            if (key == "buffer") return PropertyDescriptor.Data(JsValue.From(Buffer));
            if (TryIndex(key, out var i) && i < Length) return PropertyDescriptor.Data(Read(i), enumerable: true);
            return base.GetOwnProperty(key);
        }
        public override bool Set(string key, JsValue value, JsValue receiver)
        {
            if (TryIndex(key, out var i)) { if (i < Length) Write(i, value); return true; }
            return base.Set(key, value, receiver);
        }
        public override bool DefineOwnProperty(string key, PropertyDescriptor descriptor)
        {
            if (TryIndex(key, out var i)) { if (i >= Length || descriptor.IsAccessorDescriptor) return false; if (descriptor.Value is { } v) Write(i, v); return true; }
            if (key is "length" or "byteLength" or "byteOffset" or "buffer") return false;
            return base.DefineOwnProperty(key, descriptor);
        }
        // Integer element reads are converted to JS Number; BigInt views retain BigInt values.
        internal JsValue Read(int index)
        {
            var offset = ByteOffset + index * BytesPerElement;
            var span = Buffer.Bytes.AsSpan(offset, BytesPerElement);
            return ElementKind switch
            {
                Kind.Int8 => JsValue.From((double)(sbyte)span[0]), Kind.Uint8 or Kind.Uint8Clamped => JsValue.From((double)span[0]),
                Kind.Int16 => JsValue.From((double)BitConverter.ToInt16(span)), Kind.Uint16 => JsValue.From((double)BitConverter.ToUInt16(span)),
                Kind.Int32 => JsValue.From((double)BitConverter.ToInt32(span)), Kind.Uint32 => JsValue.From((double)BitConverter.ToUInt32(span)),
                Kind.Float32 => JsValue.From((double)BitConverter.ToSingle(span)), Kind.Float64 => JsValue.From(BitConverter.ToDouble(span)),
                Kind.BigInt64 => JsValue.From(new BigInt(BitConverter.ToInt64(span))),
                Kind.BigUint64 => JsValue.From(new BigInt(BitConverter.ToUInt64(span))),
                _ => throw new InvalidOperationException("Unknown typed array element kind")
            };
        }
        // Writes apply the element kind's conversion, wrapping integer values or clamping Uint8Clamped.
        internal void Write(int index, JsValue value)
        {
            var offset = ByteOffset + index * BytesPerElement;
            var span = Buffer.Bytes.AsSpan(offset, BytesPerElement);
            value = AbstractOperations.ToPrimitive(value);
            if (ElementKind is Kind.BigInt64 or Kind.BigUint64)
            {
                var integer = value.Type switch
                {
                    JsType.BigInt => (BigInt)value.Data!,
                    JsType.Boolean => value.ToBoolean() ? BigInt.One : BigInt.Zero,
                    JsType.String when AbstractOperations.TryParseBigInt((string)value.Data!, out var parsed) => parsed,
                    JsType.String => throw new JsException(JsValue.From(new JsError("SyntaxError", "Invalid BigInt string"))),
                    _ => throw JsException.TypeError("Cannot convert typed array element to BigInt")
                };
                BitConverter.TryWriteBytes(span, (ulong)(integer & new BigInt(ulong.MaxValue)));
                return;
            }
            var n = value.ToNumber();
            switch (ElementKind)
            {
                case Kind.Int8 or Kind.Uint8: span[0] = (byte)WrapInteger(n, 8); break;
                case Kind.Uint8Clamped: span[0] = (byte)Math.Clamp(double.IsNaN(n) ? 0 : Math.Round(n, MidpointRounding.ToEven), 0, 255); break;
                case Kind.Int16 or Kind.Uint16: BitConverter.TryWriteBytes(span, (ushort)WrapInteger(n, 16)); break;
                case Kind.Int32 or Kind.Uint32: BitConverter.TryWriteBytes(span, WrapInteger(n, 32)); break;
                case Kind.Float32: BitConverter.TryWriteBytes(span, (float)n); break;
                case Kind.Float64: BitConverter.TryWriteBytes(span, n); break;
            }
        }
    }

    public enum Kind { Int8, Uint8, Uint8Clamped, Int16, Uint16, Int32, Uint32, Float32, Float64, BigInt64, BigUint64 }
    private static int Size(Kind kind) => kind switch { Kind.Int8 or Kind.Uint8 or Kind.Uint8Clamped => 1, Kind.Int16 or Kind.Uint16 => 2, Kind.Int32 or Kind.Uint32 or Kind.Float32 => 4, _ => 8 };

    public static JsObject Install(Action<string, JsValue> setGlobal, JsObject objectPrototype)
    {
        var prototypes = new Dictionary<Kind, JsObject>();
        var common = new JsObject { Prototype = objectPrototype };
        Define(common, "at", 1, (self, a) => { var v = RequireView(self); var i = (int)Integer(Arg(a, 0)); if (i < 0) i += v.Length; return (uint)i < (uint)v.Length ? v.Read(i) : JsValue.Undefined; });
        Define(common, "set", 1, (self, a) => { var v = RequireView(self); if (Arg(a, 0).Data is not JsObject source) throw JsException.TypeError("Source must be array-like"); var offset = Math.Max(0, (int)Integer(Arg(a, 1))); var count = (int)Math.Clamp(Integer(source.Get("length")), 0, int.MaxValue); if (offset + count > v.Length) throw JsException.RangeError("Source is too large"); var copy = Enumerable.Range(0, count).Select(i => source.Get(i.ToString(CultureInfo.InvariantCulture))).ToArray(); for (var i = 0; i < count; i++) v.Write(offset + i, copy[i]); return JsValue.Undefined; });
        Define(common, "slice", 2, (self, a) => { var v = RequireView(self); var (start, end) = Bounds(v.Length, a); var result = NewView(v.ElementKind, end - start, prototypes[v.ElementKind]); for (var i = start; i < end; i++) result.Write(i - start, v.Read(i)); return JsValue.From(result); });
        Define(common, "subarray", 2, (self, a) => { var v = RequireView(self); var (start, end) = Bounds(v.Length, a); var result = new ViewObject(v.Buffer, end - start, v.ElementKind, prototypes[v.ElementKind], v.ByteOffset + start * v.BytesPerElement); return JsValue.From(result); });
        Define(common, "fill", 1, (self,a) => { var v=RequireView(self); var (s,e)=Bounds(v.Length,[Arg(a,1),Arg(a,2)]); for(var i=s;i<e;i++) v.Write(i,Arg(a,0)); return JsValue.From(v); });
        Define(common, "copyWithin", 2, (self,a) => { var v=RequireView(self); var target=Index(Arg(a,0),v.Length); var start=Index(Arg(a,1),v.Length); var end=a.Length<3?v.Length:Index(Arg(a,2),v.Length); var count=Math.Min(Math.Max(end-start,0),v.Length-target); var temp=Enumerable.Range(0,count).Select(i=>v.Read(start+i)).ToArray(); for(var i=0;i<count;i++)v.Write(target+i,temp[i]); return JsValue.From(v); });
        Define(common, "includes", 1, (self,a) => { var v=RequireView(self); var start=Math.Max(0,Index(Arg(a,1),v.Length)); for(var i=start;i<v.Length;i++) if(SameValueZero(v.Read(i),Arg(a,0)))return JsValue.From(true); return JsValue.From(false); });
        Define(common, "indexOf", 1, (self,a) => { var v=RequireView(self); for(var i=Math.Max(0,Index(Arg(a,1),v.Length));i<v.Length;i++)if(AbstractOperations.StrictEqual(v.Read(i),Arg(a,0)))return JsValue.From((double)i);return JsValue.From(-1d); });
        Define(common, "join", 1, (self,a) => { var v=RequireView(self); var sep=Arg(a,0).Type==JsType.Undefined?",":Arg(a,0).ToJsString(); return JsValue.From(string.Join(sep,Enumerable.Range(0,v.Length).Select(i=>v.Read(i).ToJsString()))); });
        Define(common, "forEach", 1, (self,a)=>Iterate(self,a,"forEach")); Define(common,"map",1,(self,a)=>Iterate(self,a,"map")); Define(common,"filter",1,(self,a)=>Iterate(self,a,"filter")); Define(common,"every",1,(self,a)=>Iterate(self,a,"every")); Define(common,"some",1,(self,a)=>Iterate(self,a,"some")); Define(common,"find",1,(self,a)=>Iterate(self,a,"find")); Define(common,"findIndex",1,(self,a)=>Iterate(self,a,"findIndex"));
        Define(common, "findLast", 1, (self, args) => FindLast(self, args, returnIndex: false));
        Define(common, "findLastIndex", 1, (self, args) => FindLast(self, args, returnIndex: true));
        Define(common, "reverse", 0, (self, _) => { var v = RequireView(self); for (var i = 0; i < v.Length / 2; i++) { var j = v.Length - i - 1; var x = v.Read(i); v.Write(i, v.Read(j)); v.Write(j, x); } return JsValue.From(v); });
        Define(common, "toReversed", 0, (self, _) => { var v = RequireView(self); var r = NewView(v.ElementKind, v.Length, prototypes[v.ElementKind]); for (var i = 0; i < v.Length; i++) r.Write(i, v.Read(v.Length-i-1)); return JsValue.From(r); });
        Define(common, "toSorted", 1, (self,a)=>{var v=RequireView(self);var r=NewView(v.ElementKind,v.Length,prototypes[v.ElementKind]);for(var i=0;i<v.Length;i++)r.Write(i,v.Read(i));Sort(r,Arg(a,0));return JsValue.From(r);});
        Define(common, "sort", 1, (self,a)=>{var v=RequireView(self);Sort(v,Arg(a,0));return JsValue.From(v);});
        Define(common, "with", 2, (self, a) => { var v = RequireView(self); var i = (int)Integer(Arg(a, 0)); if (i < 0) i += v.Length; if ((uint)i >= (uint)v.Length) throw JsException.RangeError("Invalid typed array index"); var r = NewView(v.ElementKind, v.Length, prototypes[v.ElementKind]); for (var j=0;j<v.Length;j++) r.Write(j,j==i?Arg(a,1):v.Read(j)); return JsValue.From(r); });
        var bufferPrototype = new JsObject { Prototype = objectPrototype };
        var bufferCtor = new JsFunction(a => { var n = Integer(Arg(a,0)); if (n < 0 || n > int.MaxValue) throw JsException.RangeError("Invalid ArrayBuffer length"); return JsValue.From(new BufferObject((int)n) { Prototype = bufferPrototype }); }, 1) { Prototype = objectPrototype, NativeConstruct = a => JsValue.From(new BufferObject((int)Math.Clamp(Integer(Arg(a,0)),0,int.MaxValue)) { Prototype = bufferPrototype }) };
        bufferCtor.DefineOwnProperty("prototype", PropertyDescriptor.Data(JsValue.From(bufferPrototype)));
        setGlobal("ArrayBuffer", JsValue.From(bufferCtor));
        foreach (Kind kind in Enum.GetValues<Kind>())
        {
            var prototype = new JsObject { Prototype = common }; prototypes[kind] = prototype;
            var captured = kind; var name = captured.ToString() switch { "Uint8" => "Uint8Array", "Uint8Clamped" => "Uint8ClampedArray", "Uint16" => "Uint16Array", "Uint32" => "Uint32Array", "Float32" => "Float32Array", "Float64" => "Float64Array", _ => captured + "Array" };
            JsValue Construct(JsValue[] a) { var n = Arg(a,0); if (n.Data is BufferObject b) return JsValue.From(new ViewObject(b, b.ByteLength / Size(captured), captured, prototype)); var len = n.Data is JsArray arr ? arr.Length : Math.Clamp((int)Integer(n),0,1_000_000); var view = NewView(captured,len,prototype); if (n.Data is JsArray src) for(var i=0;i<len;i++) view.Write(i,src.Get(i.ToString(CultureInfo.InvariantCulture))); return JsValue.From(view); }
            var ctor = new JsFunction(Construct,1) { Prototype = objectPrototype, NativeConstruct = Construct }; ctor.DefineOwnProperty("prototype", PropertyDescriptor.Data(JsValue.From(prototype))); ctor.DefineOwnProperty("BYTES_PER_ELEMENT", PropertyDescriptor.Data(JsValue.From((double)Size(captured)))); prototype.DefineOwnProperty("BYTES_PER_ELEMENT", PropertyDescriptor.Data(JsValue.From((double)Size(captured)))); setGlobal(name,JsValue.From(ctor));
        }
        return common;

        ViewObject NewView(Kind kind, int length, JsObject proto) => new(new BufferObject(length * Size(kind)), length, kind, proto);
    }

    private static JsValue Iterate(JsValue receiver, JsValue[] args, string operation)
    {
        var v=RequireView(receiver); var callback=Arg(args,0).Data as JsFunction ?? throw JsException.TypeError("Callback must be callable"); var output=new List<JsValue>();
        for(var i=0;i<v.Length;i++){var x=v.Read(i);var r=callback.Call(Arg(args,1),[x,JsValue.From((double)i),receiver]); if(operation=="map") output.Add(r); if(operation=="filter"&&r.ToBoolean())output.Add(x); if(operation=="every"&&!r.ToBoolean())return JsValue.From(false);if(operation=="some"&&r.ToBoolean())return JsValue.From(true);if(operation=="find"&&r.ToBoolean())return x;if(operation=="findIndex"&&r.ToBoolean())return JsValue.From((double)i);}
        if(operation=="every")return JsValue.From(true);if(operation=="some")return JsValue.From(false);if(operation=="findIndex")return JsValue.From(-1d);if(operation is "find" or "forEach")return JsValue.Undefined;
        var kind=v.ElementKind;var b=new BufferObject(output.Count*Size(kind));var result=new ViewObject(b,output.Count,kind,v.Prototype);for(var i=0;i<output.Count;i++)result.Write(i,output[i]);return JsValue.From(result);
    }
    private static JsValue FindLast(JsValue receiver, JsValue[] args, bool returnIndex)
    {
        var view = RequireView(receiver);
        if (Arg(args, 0).Data is not JsFunction callback)
            throw JsException.TypeError("Callback must be callable");
        for (var i = view.Length - 1; i >= 0; i--)
        {
            var value = view.Read(i);
            var index = JsValue.From((double)i);
            if (callback.Call(Arg(args, 1), [value, index, receiver]).ToBoolean())
                return returnIndex ? index : value;
        }
        return returnIndex ? JsValue.From(-1d) : JsValue.Undefined;
    }

    private static void Sort(ViewObject view, JsValue comparator)
    {
        var callback = comparator.Data as JsFunction;
        if (callback is null && comparator.Type != JsType.Undefined)
            throw JsException.TypeError("Comparator must be callable");

        var values = Enumerable.Range(0, view.Length).Select(view.Read).ToArray();
        var scratch = new JsValue[values.Length];
        MergeSort(0, values.Length);
        for (var i = 0; i < values.Length; i++) view.Write(i, values[i]);

        void MergeSort(int start, int end)
        {
            if (end - start < 2) return;
            var middle = start + (end - start) / 2;
            MergeSort(start, middle);
            MergeSort(middle, end);
            var left = start;
            var right = middle;
            for (var i = start; i < end; i++)
                scratch[i] = right == end || (left < middle && Compare(values[left], values[right]) <= 0)
                    ? values[left++] : values[right++];
            Array.Copy(scratch, start, values, start, end - start);
        }

        int Compare(JsValue x, JsValue y)
        {
            if (callback is not null)
            {
                var order = AbstractOperations.ToPrimitive(callback.Call(JsValue.Undefined, [x, y])).ToNumber();
                return order < 0 ? -1 : order > 0 ? 1 : 0;
            }
            if (x.Data is BigInt leftInteger && y.Data is BigInt rightInteger)
                return leftInteger.CompareTo(rightInteger);
            var a = x.ToNumber();
            var b = y.ToNumber();
            if (double.IsNaN(a)) return double.IsNaN(b) ? 0 : 1;
            if (double.IsNaN(b)) return -1;
            if (a == 0 && b == 0) return BitConverter.DoubleToInt64Bits(a).CompareTo(BitConverter.DoubleToInt64Bits(b));
            return a.CompareTo(b);
        }
    }
    private static int Index(JsValue value,int length){var n=Integer(value);return (int)Math.Clamp(n<0?length+n:n,0,length);}
    private static bool SameValueZero(JsValue x,JsValue y)=>AbstractOperations.StrictEqual(x,y)||(x.Type==JsType.Number&&y.Type==JsType.Number&&double.IsNaN(x.ToNumber())&&double.IsNaN(y.ToNumber()));
    private static bool TryIndex(string key, out int index) => int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index >= 0 && index.ToString(CultureInfo.InvariantCulture) == key;
    private static void Define(JsObject target,string name,int arity,Func<JsValue,JsValue[],JsValue> call) { target.DefineOwnProperty(name,PropertyDescriptor.Data(JsValue.From(new JsFunction(call,arity)),writable:true,configurable:true)); }
    private static JsValue Arg(JsValue[] a,int i)=>i<a.Length?a[i]:JsValue.Undefined;
    private static ViewObject RequireView(JsValue v)=>v.Data as ViewObject??throw JsException.TypeError("TypedArray method called on incompatible receiver");
    private static double Integer(double n)=>double.IsNaN(n)||n==0?0:Math.Truncate(n);
    private static double Integer(JsValue v)=>Integer(v.ToNumber());
    private static uint WrapInteger(double value, int bits)
    {
        if (!double.IsFinite(value)) return 0;
        double modulus = 1UL << bits;
        var remainder = Math.Truncate(value) % modulus;
        return (uint)(remainder < 0 ? remainder + modulus : remainder);
    }
    private static (int Start,int End) Bounds(int length,JsValue[] a) { var s=(int)Integer(Arg(a,0)); var e=Arg(a,1).Type==JsType.Undefined?length:(int)Integer(Arg(a,1)); return (Math.Clamp(s<0?length+s:s,0,length),Math.Clamp(e<0?length+e:e,0,length)); }
}