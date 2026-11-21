using System.Globalization;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using ShoneJs.Runtime;

namespace ShoneHtml;

internal sealed class HtmlImage
{
    internal long Revision;
    internal bool Pending;
    internal byte[]? Bytes;
    internal int NaturalWidth;
    internal int NaturalHeight;
    internal string CurrentSrc = "";

    internal void Load(byte[] bytes, HtmlRendererPixels image, string source)
    {
        Bytes = bytes;
        NaturalWidth = image.Width;
        NaturalHeight = image.Height;
        CurrentSrc = source;
    }

    internal void Reset()
    {
        Bytes = null;
        NaturalWidth = NaturalHeight = 0;
        CurrentSrc = "";
    }
}

internal sealed partial class HtmlScriptHost
{
    private readonly SemaphoreSlim _imageDownloads = new(4);
    private const int DefaultImageResponseLimit = 4 * 1024 * 1024;
    private const int DefaultImagePageLimit = 16 * 1024 * 1024;
    private const int DefaultImageRequestLimit = 48;

    private void BindImage()
    {
        Constructor("Image", 2, args =>
        {
            var image = (IHtmlImageElement)AccountNode(_page.Document.CreateElement("img"));
            if (args.Length > 0) SetImageDimension(image, "width", args[0]);
            if (args.Length > 1) SetImageDimension(image, "height", args[1]);
            return Wrap(image);
        }, _imagePrototype);
        Constructor("HTMLImageElement", 0, _ => throw TypeError("Illegal constructor"), _imagePrototype);
        Accessor(_imagePrototype, "src", e =>
        {
            var value = Image(e).GetAttribute("src");
            return JsValue.From(value is null ? "" : _page.ResolveAddress(value)?.AbsoluteUri ?? value);
        }, (e, v) => SetImageSource(Image(e), v.ToJsString()));
        Accessor(_imagePrototype, "currentSrc", e => JsValue.From(_page.ImageState(Image(e)).CurrentSrc));
        Accessor(_imagePrototype, "complete", e => JsValue.From(!_page.ImageState(Image(e)).Pending));
        Accessor(_imagePrototype, "naturalWidth", e => JsValue.From((double)_page.ImageState(Image(e)).NaturalWidth));
        Accessor(_imagePrototype, "naturalHeight", e => JsValue.From((double)_page.ImageState(Image(e)).NaturalHeight));
        Accessor(_imagePrototype, "alt", e => JsValue.From(Image(e).GetAttribute("alt") ?? ""), (e, v) =>
        {
            var text = v.ToJsString();
            if (Renderer is not null) CheckDomText(text, "Attribute value");
            Image(e).SetAttribute("alt", text);
            _page.MarkDirty();
        });
        foreach (var dimension in new[] { "width", "height" })
            Accessor(_imagePrototype, dimension, e =>
            {
                var image = Image(e);
                var state = _page.ImageState(image);
                return JsValue.From(uint.TryParse(image.GetAttribute(dimension), NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                    ? (double)size : dimension == "width" ? state.NaturalWidth : state.NaturalHeight);
            }, (e, v) => SetImageDimension(Image(e), dimension, v));
    }

    private static IHtmlImageElement Image(JsValue receiver) => Element(receiver) as IHtmlImageElement ?? throw TypeError("Expected an image element");

    private void SetImageDimension(IHtmlImageElement image, string attribute, JsValue value)
    {
        var number = Math.Truncate(value.ToNumber());
        var size = double.IsFinite(number) ? (uint)((number % 4294967296d + 4294967296d) % 4294967296d) : 0;
        image.SetAttribute(attribute, size.ToString(CultureInfo.InvariantCulture));
        _page.MarkDirty();
    }

    private void SetImageSource(IHtmlImageElement image, string value)
    {
        if (Renderer is not null) CheckDomText(value, "Attribute value");
        image.SetAttribute("src", value);
        UpdateImageSource(image);
        _page.MarkDirty();
    }

    private void UpdateImageSource(IHtmlImageElement element)
    {
        var state = _page.ImageState(element);
        var revision = Interlocked.Increment(ref state.Revision);
        state.Reset();
        var value = element.GetAttribute("src");
        state.Pending = !string.IsNullOrWhiteSpace(value);
        if (value is null) return;

        var source = value.Trim();
        var address = source.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? source
            : source.Length != 0 && _page.ResolveAddress(source) is { } resolved ? resolved.AbsoluteUri : null;
        PostTask("image request", () =>
        {
            // A source assigned again during the same script turn supersedes this request before it starts.
            if (state.Revision != revision) return;
            if (address is null || !_page.ReserveImageRequest(BrowserOptions?.MaximumPageResources ?? DefaultImageRequestLimit))
                FinishImage(element, state, revision, address, null);
            else
                _ = DownloadImageAsync(element, state, revision, address, BrowserOptions?.MaximumResponseBytes ?? DefaultImageResponseLimit);
        });
    }

    private async Task DownloadImageAsync(IHtmlImageElement element, HtmlImage state, long revision, string address, int limit)
    {
        var lifetime = _page.Lifetime;
        try
        {
            await _imageDownloads.WaitAsync(lifetime).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref state.Revision) != revision) return;
                var request = address.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? address : HtmlResources.WithoutFragment(new Uri(address)).AbsoluteUri;
                HtmlResource? resource;
                try
                {
                    resource = await _page.LoadImageResourceAsync(request, limit, lifetime).ConfigureAwait(false);
                }
                catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException ||
                    error is OperationCanceledException && !lifetime.IsCancellationRequested)
                {
                    resource = null;
                }
                if (Volatile.Read(ref state.Revision) != revision) return;

                // Keep the slot until the owner consumes the response so queued buffers stay bounded too.
                var consumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (PostTask("image response", () =>
                {
                    try { FinishImage(element, state, revision, address, resource); }
                    finally { consumed.TrySetResult(); }
                }))
                    await consumed.Task.WaitAsync(lifetime).ConfigureAwait(false);
            }
            finally { _imageDownloads.Release(); }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private void FinishImage(IHtmlImageElement element, HtmlImage state, long revision, string? address, HtmlResource? resource)
    {
        if (state.Revision != revision) return;
        state.Pending = false;
        if (resource is { IsSuccess: true } &&
            _page.CountResourceBytes(resource.Bytes.Length, BrowserOptions?.MaximumPageResourceBytes ?? DefaultImagePageLimit) &&
            _page.DecodePageImage(resource.Bytes, _page.Lifetime) is { } image)
            state.Load(resource.Bytes, image, address!);
        _page.MarkDirty();
        DispatchImageEvent(element, state.Bytes is not null);
    }
}
