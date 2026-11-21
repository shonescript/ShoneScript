using System.Globalization;
using System.Runtime.CompilerServices;
using AngleSharp.Dom;
using Aprillz.MewUI;
using ShoneJs.Runtime;
using ShoneJs.Runtime.Builtins;

namespace ShoneHtml;

internal sealed class HtmlCanvas(HtmlPage page, IElement element)
{
    internal const int MaximumDimension = 2048;
    internal const int MaximumPixels = 1_048_576;
    private const int MaximumSavedStates = 64;
    private readonly Stack<DrawState> _saved = new();
    private DrawState _state = new(Color.FromRgb(0, 0, 0), 1);
    private byte[]? _rgba;
    private HtmlRendererPixels? _published;

    internal int Width => GetWidth(element);
    internal int Height => GetHeight(element);
    internal int RetainedBytes => _rgba?.Length ?? 0;
    internal string FillStyle => _state.Fill.A == 255
        ? $"#{_state.Fill.R:x2}{_state.Fill.G:x2}{_state.Fill.B:x2}"
        : FormattableString.Invariant($"rgba({_state.Fill.R}, {_state.Fill.G}, {_state.Fill.B}, {_state.Fill.A / 255d:0.###})");
    internal double GlobalAlpha
    {
        get => _state.Alpha;
        set { if (value is >= 0 and <= 1) _state = _state with { Alpha = value }; }
    }

    internal static bool IsCanvas(IElement element) => element.LocalName == "canvas" && element.NamespaceUri == NamespaceNames.HtmlUri;
    internal static int GetWidth(IElement element) => ParseDimension(element.GetAttribute("width"), 300);
    internal static int GetHeight(IElement element) => ParseDimension(element.GetAttribute("height"), 150);

    internal static int ParseDimension(string? value, int fallback)
    {
        var text = value.AsSpan().TrimStart(" \t\r\n\f");
        if (!text.IsEmpty && text[0] == '+') text = text[1..];
        if (text.IsEmpty || !char.IsAsciiDigit(text[0])) return fallback;
        var length = 1;
        while (length < text.Length && char.IsAsciiDigit(text[length])) length++;
        return int.TryParse(text[..length], NumberStyles.None, CultureInfo.InvariantCulture, out var dimension) ? dimension : fallback;
    }

    internal static bool CanAllocate(int width, int height) => width is > 0 and <= MaximumDimension &&
        height is > 0 and <= MaximumDimension && width * height <= MaximumPixels;

    internal void Reset()
    {
        _state = new(Color.FromRgb(0, 0, 0), 1);
        _saved.Clear();
        _rgba = null;
        _published = null;
    }

    internal void SetFillStyle(string value)
    {
        if (value.Length <= 128 && HtmlStyles.TryColor(value.AsSpan().Trim(), out var color))
            _state = _state with { Fill = color };
    }

    internal void Save()
    {
        if (_saved.Count < MaximumSavedStates) _saved.Push(_state);
    }

    internal void Restore()
    {
        if (_saved.TryPop(out var state)) _state = state;
    }

    internal HtmlRendererPixels? GetPixels() => _rgba is null ? null : _published ??= new(Width, Height, (byte[])_rgba.Clone());

    internal void DrawRect(double x, double y, double width, double height, bool clear)
    {
        var canvasWidth = Width;
        var canvasHeight = Height;
        if (!CanAllocate(canvasWidth, canvasHeight) || !double.IsFinite(x) || !double.IsFinite(y) ||
            !double.IsFinite(width) || !double.IsFinite(height) || width == 0 || height == 0) return;
        var left = Math.Clamp(Math.Min(x, x + width), 0, canvasWidth);
        var right = Math.Clamp(Math.Max(x, x + width), 0, canvasWidth);
        var top = Math.Clamp(Math.Min(y, y + height), 0, canvasHeight);
        var bottom = Math.Clamp(Math.Max(y, y + height), 0, canvasHeight);
        var alpha = _state.Fill.A / 255d * _state.Alpha;
        if (left >= right || top >= bottom || (clear ? _rgba is null : alpha == 0)) return;
        var startX = (int)Math.Floor(left);
        var endX = (int)Math.Ceiling(right);
        var startY = (int)Math.Floor(top);
        var endY = (int)Math.Ceiling(bottom);
        Interpreter.ChargeNativeWork((long)(endX - startX) * (endY - startY));
        if (!EnsurePixels(canvasWidth, canvasHeight)) return;

        for (var row = startY; row < endY; row++)
        {
            var vertical = Math.Min(bottom, row + 1) - Math.Max(top, row);
            for (var column = startX; column < endX; column++)
            {
                var coverage = vertical * (Math.Min(right, column + 1) - Math.Max(left, column));
                var pixel = _rgba.AsSpan((row * canvasWidth + column) * 4, 4);
                if (clear)
                {
                    pixel[3] = Byte(pixel[3] * (1 - coverage));
                    if (pixel[3] == 0) pixel.Clear();
                }
                else Blend(pixel, _state.Fill, alpha * coverage);
            }
        }
        PixelsChanged();
    }

    internal void ReadPixels(double x, double y, int width, int height, Span<byte> destination)
    {
        if (_rgba is null) return;
        var canvasWidth = Width;
        if (!CopyRegion(x, y, width, height, out var canvasX, out var canvasY, out var imageX, out var imageY,
                out var columns, out var rows)) return;
        Interpreter.ChargeNativeWork((long)columns * rows / 4);
        for (var row = 0; row < rows; row++)
            _rgba.AsSpan(((canvasY + row) * canvasWidth + canvasX) * 4, columns * 4)
                .CopyTo(destination.Slice(((imageY + row) * width + imageX) * 4));
    }

    internal void WritePixels(double x, double y, int width, int height, ReadOnlySpan<byte> source,
        int dirtyX, int dirtyY, int dirtyWidth, int dirtyHeight)
    {
        var canvasWidth = Width;
        var canvasHeight = Height;
        if (!CanAllocate(canvasWidth, canvasHeight) || !CopyRegion(x + dirtyX, y + dirtyY, dirtyWidth, dirtyHeight,
                out var canvasX, out var canvasY, out var imageX, out var imageY, out var columns, out var rows)) return;
        Interpreter.ChargeNativeWork((long)columns * rows / 4);
        if (!EnsurePixels(canvasWidth, canvasHeight)) return;
        for (var row = 0; row < rows; row++)
            source.Slice(((dirtyY + imageY + row) * width + dirtyX + imageX) * 4, columns * 4)
                .CopyTo(_rgba.AsSpan(((canvasY + row) * canvasWidth + canvasX) * 4));
        PixelsChanged();
    }

    private bool CopyRegion(double x, double y, int width, int height, out int canvasX, out int canvasY,
        out int imageX, out int imageY, out int columns, out int rows)
    {
        var canvasWidth = Width;
        var canvasHeight = Height;
        canvasX = (int)Math.Clamp(x, 0, canvasWidth);
        canvasY = (int)Math.Clamp(y, 0, canvasHeight);
        imageX = (int)Math.Clamp(-x, 0, width);
        imageY = (int)Math.Clamp(-y, 0, height);
        columns = Math.Min(width - imageX, canvasWidth - canvasX);
        rows = Math.Min(height - imageY, canvasHeight - canvasY);
        return columns > 0 && rows > 0;
    }

    private bool EnsurePixels(int width, int height)
    {
        if (_rgba is not null) return true;
        var length = width * height * 4;
        if (!page.ReserveCanvas(this, length)) return false;
        Interpreter.ChargeNativeWork(length / 16);
        _rgba = new byte[length];
        return true;
    }

    private void PixelsChanged()
    {
        _published = null;
        page.MarkDirty();
    }

    private static void Blend(Span<byte> pixel, Color color, double sourceAlpha)
    {
        var destinationAlpha = pixel[3] / 255d * (1 - sourceAlpha);
        var outputAlpha = sourceAlpha + destinationAlpha;
        if (outputAlpha == 0) return;
        pixel[0] = Byte((color.R * sourceAlpha + pixel[0] * destinationAlpha) / outputAlpha);
        pixel[1] = Byte((color.G * sourceAlpha + pixel[1] * destinationAlpha) / outputAlpha);
        pixel[2] = Byte((color.B * sourceAlpha + pixel[2] * destinationAlpha) / outputAlpha);
        pixel[3] = Byte(outputAlpha * 255);
        if (pixel[3] == 0) pixel.Clear();
    }

    private static byte Byte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 255), MidpointRounding.AwayFromZero);
    private readonly record struct DrawState(Color Fill, double Alpha);
}

public sealed partial class HtmlPage
{
    private const int MaximumCanvasBytes = 8 * 1024 * 1024;
    private const int MaximumCanvasSurfaces = 128;
    private readonly ConditionalWeakTable<IElement, HtmlCanvas> _canvases = new();
    private readonly List<WeakReference<HtmlCanvas>> _canvasSurfaces = [];

    internal HtmlCanvas GetCanvas(IElement element) => _canvases.GetValue(element, e => new(this, e));
    internal HtmlRendererPixels? GetCanvasPixels(IElement element) => _canvases.TryGetValue(element, out var canvas) ? canvas.GetPixels() : null;
    internal void ResetCanvas(IElement element)
    {
        if (_canvases.TryGetValue(element, out var canvas)) canvas.Reset();
    }

    internal bool ReserveCanvas(HtmlCanvas canvas, int bytes)
    {
        var used = 0;
        for (var i = _canvasSurfaces.Count - 1; i >= 0; i--)
        {
            if (_canvasSurfaces[i].TryGetTarget(out var other) && other.RetainedBytes != 0)
                used += other.RetainedBytes;
            else
                _canvasSurfaces.RemoveAt(i);
        }
        if (bytes > MaximumCanvasBytes - used || _canvasSurfaces.Count >= MaximumCanvasSurfaces) return false;
        _canvasSurfaces.Add(new(canvas));
        return true;
    }
}

internal sealed partial class HtmlScriptHost
{
    private const int MaximumImageDataBytes = 32 * 1024 * 1024;
    private const int MaximumImageDataBuffers = 4096;
    private readonly ConditionalWeakTable<IElement, JsObject> _canvasContexts = new();
    private readonly List<(WeakReference<TypedArrayBuiltins.BufferObject> Buffer, int Bytes)> _canvasImageDataBuffers = [];
    private int _canvasImageDataBytes;
    private JsObject _canvasContextPrototype = null!;
    private JsObject _imageDataPrototype = null!;
    private JsObject _canvasArrayPrototype = null!;
    private JsObject _canvasBufferPrototype = null!;

    private void BindCanvas()
    {
        _canvasArrayPrototype = (JsObject)((JsObject)_engine.GetGlobal("Uint8ClampedArray").Data!).Get("prototype").Data!;
        _canvasBufferPrototype = (JsObject)((JsObject)_engine.GetGlobal("ArrayBuffer").Data!).Get("prototype").Data!;
        _canvasContextPrototype = NewObject();
        _imageDataPrototype = NewObject();
        Accessor(_canvasPrototype, "width", e => JsValue.From((double)HtmlCanvas.GetWidth(CanvasElement(e))),
            (e, v) => SetCanvasDimension(CanvasElement(e), "width", v));
        Accessor(_canvasPrototype, "height", e => JsValue.From((double)HtmlCanvas.GetHeight(CanvasElement(e))),
            (e, v) => SetCanvasDimension(CanvasElement(e), "height", v));
        Method(_canvasPrototype, "getContext", 1, (e, a) =>
        {
            var element = CanvasElement(e);
            if (Arg(a, 0).ToJsString() != "2d") return JsValue.Null;
            if (_canvasContexts.TryGetValue(element, out var existing)) return JsValue.From(existing);
            var readFrequently = Arg(a, 1).Data is JsObject options && options.Get("willReadFrequently").ToBoolean();
            return JsValue.From(_canvasContexts.GetValue(element, key => new CanvasContext(_page.GetCanvas(key), key, readFrequently)
            {
                Prototype = _canvasContextPrototype
            }));
        });
        Accessor(_canvasContextPrototype, "canvas", e => Wrap(Context2D(e).Element));
        Accessor(_canvasContextPrototype, "fillStyle", e => JsValue.From(Context2D(e).Canvas.FillStyle),
            (e, v) => Context2D(e).Canvas.SetFillStyle(v.ToJsString()));
        Accessor(_canvasContextPrototype, "globalAlpha", e => JsValue.From(Context2D(e).Canvas.GlobalAlpha),
            (e, v) => Context2D(e).Canvas.GlobalAlpha = v.ToNumber());
        Method(_canvasContextPrototype, "save", 0, (e, _) => { Context2D(e).Canvas.Save(); return JsValue.Undefined; });
        Method(_canvasContextPrototype, "restore", 0, (e, _) => { Context2D(e).Canvas.Restore(); return JsValue.Undefined; });
        Method(_canvasContextPrototype, "fillRect", 4, (e, a) => DrawRect(e, a, clear: false));
        Method(_canvasContextPrototype, "clearRect", 4, (e, a) => DrawRect(e, a, clear: true));
        Method(_canvasContextPrototype, "getContextAttributes", 0, (e, _) =>
        {
            var context = Context2D(e);
            var attributes = NewObject();
            attributes.Set("alpha", JsValue.From(true));
            attributes.Set("colorSpace", JsValue.From("srgb"));
            attributes.Set("colorType", JsValue.From("unorm8"));
            attributes.Set("desynchronized", JsValue.From(false));
            attributes.Set("willReadFrequently", JsValue.From(context.ReadFrequently));
            return JsValue.From(attributes);
        });
        Method(_canvasContextPrototype, "createImageData", 2, (e, a) =>
        {
            Context2D(e);
            return JsValue.From(Arg(a, 0).Data is CanvasImageData template
                ? NewCanvasImageData(template.Width, template.Height)
                : NewCanvasImageData(PixelInteger(a, 0), PixelInteger(a, 1)));
        });
        Method(_canvasContextPrototype, "getImageData", 4, (e, a) =>
        {
            var canvas = Context2D(e).Canvas;
            var x = PixelInteger(a, 0);
            var y = PixelInteger(a, 1);
            var width = PixelInteger(a, 2);
            var height = PixelInteger(a, 3);
            var data = NewCanvasImageData(width, height);
            canvas.ReadPixels(Math.Min(x, x + width), Math.Min(y, y + height), data.Width, data.Height, data.Bytes);
            return JsValue.From(data);
        });
        Method(_canvasContextPrototype, "putImageData", 3, (e, a) =>
        {
            var canvas = Context2D(e).Canvas;
            if (Arg(a, 0).Data is not CanvasImageData image) throw TypeError("Expected ImageData");
            var x = PixelInteger(a, 1);
            var y = PixelInteger(a, 2);
            var (left, top, right, bottom) = (0, 0, image.Width, image.Height);
            if (a.Length != 3)
            {
                if (a.Length < 7) throw TypeError("putImageData requires three or seven arguments");
                var dx = PixelInteger(a, 3);
                var dy = PixelInteger(a, 4);
                var dw = PixelInteger(a, 5);
                var dh = PixelInteger(a, 6);
                left = (int)Math.Clamp(Math.Min(dx, dx + dw), 0, image.Width);
                top = (int)Math.Clamp(Math.Min(dy, dy + dh), 0, image.Height);
                right = (int)Math.Clamp(Math.Max(dx, dx + dw), 0, image.Width);
                bottom = (int)Math.Clamp(Math.Max(dy, dy + dh), 0, image.Height);
            }
            canvas.WritePixels(x, y, image.Width, image.Height, image.Bytes, left, top, right - left, bottom - top);
            return JsValue.Undefined;
        });
    }

    private void SetCanvasDimension(IElement element, string name, JsValue value)
    {
        const double modulus = 4_294_967_296d;
        var number = value.ToNumber();
        var unsigned = double.IsFinite(number) ? (Math.Truncate(number) % modulus + modulus) % modulus : 0;
        element.SetAttribute(name, unsigned.ToString(CultureInfo.InvariantCulture));
        ResetCanvasDimension(element, name);
        _page.MarkDirty();
    }

    private void ResetCanvasDimension(IElement element, string attribute)
    {
        if (HtmlCanvas.IsCanvas(element) && (attribute.Equals("width", StringComparison.OrdinalIgnoreCase) ||
            attribute.Equals("height", StringComparison.OrdinalIgnoreCase))) _page.ResetCanvas(element);
    }

    private static IElement CanvasElement(JsValue receiver) => Element(receiver) is { } element && HtmlCanvas.IsCanvas(element)
        ? element : throw TypeError("Expected a canvas element");
    private static CanvasContext Context2D(JsValue value) => value.Data as CanvasContext ?? throw TypeError("Expected a canvas 2D context");

    private static JsValue DrawRect(JsValue receiver, JsValue[] args, bool clear)
    {
        Context2D(receiver).Canvas.DrawRect(Arg(args, 0).ToNumber(), Arg(args, 1).ToNumber(),
            Arg(args, 2).ToNumber(), Arg(args, 3).ToNumber(), clear);
        return JsValue.Undefined;
    }

    private static double PixelInteger(JsValue[] args, int index)
    {
        var number = Arg(args, index).ToNumber();
        return double.IsFinite(number) && number is >= int.MinValue and <= int.MaxValue
            ? Math.Truncate(number) : throw TypeError("Expected a finite pixel coordinate in the signed 32-bit range");
    }

    private CanvasImageData NewCanvasImageData(double width, double height)
    {
        if (width == 0 || height == 0)
            throw new JsException(JsValue.From(new JsError("IndexSizeError", "ImageData dimensions must be nonzero")));
        width = Math.Abs(width);
        height = Math.Abs(height);
        if (width > HtmlCanvas.MaximumDimension || height > HtmlCanvas.MaximumDimension ||
            !HtmlCanvas.CanAllocate((int)width, (int)height))
            throw new JsException(JsValue.From(new JsError("RangeError", "ImageData exceeds the canvas size limit")));
        var length = (int)width * (int)height * 4;
        if (ImageDataBudgetExceeded(length))
        {
            Interpreter.ChargeNativeWork(_canvasImageDataBuffers.Count);
            _canvasImageDataBuffers.RemoveAll(entry =>
            {
                if (entry.Buffer.TryGetTarget(out _)) return false;
                _canvasImageDataBytes -= entry.Bytes;
                return true;
            });
            if (ImageDataBudgetExceeded(length))
                throw new JsException(JsValue.From(new JsError("RangeError", "Page ImageData allocation limit exceeded")));
        }
        var view = TypedArrayBuiltins.CreateClampedBytes(length, _canvasArrayPrototype, _canvasBufferPrototype);
        _canvasImageDataBuffers.Add((new(view.Buffer), length));
        _canvasImageDataBytes += length;
        return new CanvasImageData((int)width, (int)height, view) { Prototype = _imageDataPrototype };
    }

    private bool ImageDataBudgetExceeded(int length) => length > MaximumImageDataBytes - _canvasImageDataBytes ||
        _canvasImageDataBuffers.Count >= MaximumImageDataBuffers;

    private sealed class CanvasContext(HtmlCanvas canvas, IElement element, bool readFrequently) : JsObject
    {
        internal HtmlCanvas Canvas { get; } = canvas;
        internal IElement Element { get; } = element;
        internal bool ReadFrequently { get; } = readFrequently;
    }

    private sealed class CanvasImageData : JsObject
    {
        private readonly TypedArrayBuiltins.ViewObject _data;
        internal int Width { get; }
        internal int Height { get; }
        internal Span<byte> Bytes => _data.GetClampedBytes();

        internal CanvasImageData(int width, int height, TypedArrayBuiltins.ViewObject data)
        {
            Width = width;
            Height = height;
            _data = data;
            DefineOwnProperty("width", PropertyDescriptor.Data(JsValue.From((double)Width), enumerable: true));
            DefineOwnProperty("height", PropertyDescriptor.Data(JsValue.From((double)Height), enumerable: true));
            DefineOwnProperty("data", PropertyDescriptor.Data(JsValue.From(data), enumerable: true));
            DefineOwnProperty("colorSpace", PropertyDescriptor.Data(JsValue.From("srgb"), enumerable: true));
        }
    }
}
