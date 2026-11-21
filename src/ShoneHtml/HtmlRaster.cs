using System.Buffers.Binary;

namespace ShoneHtml;

internal static class HtmlRaster
{
    internal static long GetPixelCount(ReadOnlySpan<byte> bytes)
    {
        if (IsWebp(bytes))
            return TryGetWebpSize(bytes, out var width, out var height) ? CountPixels(width, height) : 0;
        ReadOnlySpan<byte> pngHeader = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82];
        if (bytes.Length >= 24 && bytes.StartsWith(pngHeader))
            return CountPixels(BinaryPrimitives.ReadInt32BigEndian(bytes[16..]), BinaryPrimitives.ReadInt32BigEndian(bytes[20..]));
        if (bytes.Length >= 13 && (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)))
            return CountPixels(BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]));
        if (bytes.Length >= 26 && bytes.StartsWith("BM"u8) && BinaryPrimitives.ReadInt32LittleEndian(bytes[14..]) >= 40)
            return CountPixels(BinaryPrimitives.ReadInt32LittleEndian(bytes[18..]), Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(bytes[22..])));
        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8)
            return 0;

        for (var offset = 2; offset + 4 <= bytes.Length;)
        {
            if (bytes[offset++] != 0xff)
                return 0;
            while (offset < bytes.Length && bytes[offset] == 0xff)
                offset++;
            if (offset + 3 > bytes.Length)
                return 0;
            var marker = bytes[offset++];
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            if (length < 2 || length > bytes.Length - offset || marker is 0xda or 0xd9)
                return 0;
            if (marker is >= 0xc0 and <= 0xcf and not (0xc4 or 0xc8 or 0xcc))
                return length < 8 ? 0 : CountPixels(BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 5)..]),
                    BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 3)..]));
            offset += length;
        }
        return 0;
    }

    internal static bool IsWebp(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..].StartsWith("WEBP"u8);

    internal static bool TryGetWebpSize(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        if (!IsWebp(bytes) || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != bytes.Length - 8)
            return false;

        var canvasWidth = 0;
        var canvasHeight = 0;
        var foundImage = false;
        for (var offset = 12; offset < bytes.Length;)
        {
            var chunk = bytes[offset..];
            if (chunk.Length < 8) return false;
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            var paddedSize = size + (long)(size & 1);
            if (paddedSize > chunk.Length - 8) return false;
            var data = chunk.Slice(8, (int)size);
            if (chunk.StartsWith("VP8X"u8))
            {
                if (offset != 12 || size != 10 || (data[0] & 0xc3) != 0 || ReadUInt24(data[1..]) != 0)
                    return false;
                canvasWidth = ReadUInt24(data[4..]) + 1;
                canvasHeight = ReadUInt24(data[7..]) + 1;
                if (CountPixels(canvasWidth, canvasHeight) == 0) return false;
            }
            else if (chunk.StartsWith("ANIM"u8) || chunk.StartsWith("ANMF"u8))
                return false;
            else if (chunk.StartsWith("VP8 "u8) || chunk.StartsWith("VP8L"u8))
            {
                if (foundImage) return false;
                if (chunk.StartsWith("VP8L"u8))
                {
                    if (size < 5 || data[0] != 0x2f) return false;
                    var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[1..]);
                    if (bits >> 29 != 0) return false;
                    width = (int)(bits & 0x3fff) + 1;
                    height = (int)((bits >> 14) & 0x3fff) + 1;
                }
                else
                {
                    if (size < 10 || (data[0] & 1) != 0 || !data[3..].StartsWith(new byte[] { 0x9d, 0x01, 0x2a }))
                        return false;
                    width = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]) & 0x3fff;
                    height = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]) & 0x3fff;
                }
                if (CountPixels(width, height) == 0 || canvasWidth != 0 && (width != canvasWidth || height != canvasHeight))
                    return false;
                foundImage = true;
            }
            offset += 8 + (int)paddedSize;
        }
        return foundImage;
    }

    private static int ReadUInt24(ReadOnlySpan<byte> bytes) => bytes[0] | bytes[1] << 8 | bytes[2] << 16;

    private static long CountPixels(long width, long height) =>
        width is > 0 and <= 4096 && height is > 0 and <= 4096 && width * height <= 8_000_000 ? width * height : 0;
}
