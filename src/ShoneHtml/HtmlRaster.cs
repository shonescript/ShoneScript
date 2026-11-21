using System.Buffers.Binary;

namespace ShoneHtml;

internal static class HtmlRaster
{
    internal static long GetPixelCount(ReadOnlySpan<byte> bytes)
    {
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

    private static long CountPixels(long width, long height) =>
        width is > 0 and <= 4096 && height is > 0 and <= 4096 && width * height <= 8_000_000 ? width * height : 0;
}
