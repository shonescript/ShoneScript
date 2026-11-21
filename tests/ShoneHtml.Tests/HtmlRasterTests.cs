using System.Buffers.Binary;

namespace ShoneHtml.Tests;

public sealed class HtmlRasterTests
{
    internal const string Webp = "UklGRhoAAABXRUJQVlA4TA4AAAAvAAAAEIGI6H8g+h8NDA==";
    private const string Lossy = "UklGRjgAAABXRUJQVlA4ICwAAACQAQCdASoBAAEAAkAuAAnWqA/gCA7A/vFNr/ylXgsbo8//LQvloXy0L/laAA==";
    private const string Extended = "UklGRlQAAABXRUJQVlA4WAoAAAAQAAAAAAAAAAAAQUxQSAIAAAAAgFZQOCAsAAAAkAEAnQEqAQABAAJALgAJ1qgP4AgOwP7xTa/8pV4LG6PP/y0L5aF8tC/5WgA=";

    [Theory]
    [InlineData(Webp, 128)]
    [InlineData(Lossy, 255)]
    [InlineData(Extended, 128)]
    public void DecodesStillWebpToStraightRgba(string encoded, int alpha)
    {
        var bytes = Convert.FromBase64String(encoded);
        Assert.Equal(1, HtmlRaster.GetPixelCount(bytes));
        var decoded = Assert.IsType<HtmlRendererPixels>(HtmlImageDecoder.Decode(bytes));
        Assert.Equal(1, decoded.Width);
        Assert.Equal(1, decoded.Height);
        Assert.Equal(new byte[] { 255, 0, 0, (byte)alpha }, decoded.Rgba);
    }

    [Fact]
    public void RejectsTruncatedOversizedOrAnimatedContainers()
    {
        var truncated = Convert.FromBase64String(Webp)[..^1];
        Assert.Equal(0, HtmlRaster.GetPixelCount(truncated));
        Assert.Null(HtmlImageDecoder.Decode(truncated));

        var invalidChunk = Convert.FromBase64String(Webp);
        BinaryPrimitives.WriteUInt32LittleEndian(invalidChunk.AsSpan(16), uint.MaxValue);
        Assert.Equal(0, HtmlRaster.GetPixelCount(invalidChunk));

        var oversized = Convert.FromBase64String(Webp);
        BinaryPrimitives.WriteUInt32LittleEndian(oversized.AsSpan(21), 0x1000);
        Assert.Equal(0, HtmlRaster.GetPixelCount(oversized));

        var animated = Convert.FromBase64String(Extended);
        animated[20] |= 2;
        Assert.Equal(0, HtmlRaster.GetPixelCount(animated));

        var mismatched = Convert.FromBase64String(Extended);
        mismatched[24] = 1;
        Assert.Equal(0, HtmlRaster.GetPixelCount(mismatched));
    }
}
