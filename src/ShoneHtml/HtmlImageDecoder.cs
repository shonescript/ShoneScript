using Aprillz.MewUI;
using Sdcb.SimdCodecs.Webp;

namespace ShoneHtml;

internal static class HtmlImageDecoder
{
    internal static HtmlRendererPixels? Decode(ReadOnlySpan<byte> bytes, CancellationToken token = default)
    {
        var count = HtmlRaster.GetPixelCount(bytes);
        if (count == 0) return null;
        token.ThrowIfCancellationRequested();
        try
        {
            if (HtmlRaster.IsWebp(bytes))
            {
                if (!HtmlRaster.TryGetWebpSize(bytes, out var webpWidth, out var webpHeight)) return null;
                var rgba = new byte[checked((int)count * 4)];
                WebpDecoder.DecodeTo(bytes, rgba, cancellationToken: token);
                return new(webpWidth, webpHeight, rgba);
            }

            using var source = ImageSource.FromBytes(bytes.ToArray());
            var width = source.PixelWidth;
            var height = source.PixelHeight;
            if (width is <= 0 or > 4096 || height is <= 0 or > 4096 || (long)width * height != count)
                return null;
            var pixels = new byte[checked((int)count * 4)];
            source.CopyPixels(pixels, width * 4);
            for (var offset = 0; offset < pixels.Length; offset += 4)
                (pixels[offset], pixels[offset + 2]) = (pixels[offset + 2], pixels[offset]);
            return new(width, height, pixels);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }
}
