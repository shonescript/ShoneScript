using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace ShoneHtml;

internal enum HtmlRendererMessage : ushort { Hello = 1, Open, Input, Poll, Result, Request, Resource }
internal enum HtmlRendererInputKind { Click = 1, Focus, Text, Change, Select, Submit, Address }
internal sealed record HtmlRendererInput(HtmlRendererInputKind Kind, int Id = 0, string? Text = null, int Index = -1);
internal sealed record HtmlRendererOpen(HtmlRendererResource Document, int MaximumLoopIterations,
    int MaximumDocumentNodes = 20_000, int MaximumDocumentDepth = 96);
internal sealed record HtmlRendererRequest(HtmlPublicRequestKind Kind, string Address, string Method, byte[]? Body,
    Dictionary<string, string>? Headers, string? Mode = null, string? Redirect = null);
internal sealed record HtmlRendererNavigation(string Address, string Method, byte[]? Body = null, string? Query = null, bool Replace = false);
internal sealed record HtmlRendererResource(string? Address, int Status, byte[]? Bytes, string? ContentType,
    Dictionary<string, string>? Headers, bool Redirected)
{
    internal static HtmlRendererResource From(HtmlResource resource) => new(resource.Address.AbsoluteUri, (int)resource.Status,
        resource.Bytes, resource.ContentType, new(resource.Headers, StringComparer.OrdinalIgnoreCase), resource.Redirected);

    internal HtmlResource ToResource()
    {
        if (!Uri.TryCreate(Address, UriKind.Absolute, out var uri) || !HtmlResources.IsHttpAddress(uri) ||
            Status is < 100 or > 599 || Bytes is null || Bytes.Length > HtmlRendererProtocol.MaximumResourceBytes || Headers is null)
            throw new HttpRequestException("Renderer resource unavailable.");
        return new(uri, (System.Net.HttpStatusCode)Status, Bytes, ContentType,
            new Dictionary<string, string>(Headers, StringComparer.OrdinalIgnoreCase), Redirected);
    }
}
internal sealed record HtmlRendererResult(HtmlRendererSnapshot? Snapshot, HtmlRendererNavigation? Navigation, string? Error, int FocusedId = 0);

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 12)]
[JsonSerializable(typeof(HtmlRendererOpen))]
[JsonSerializable(typeof(HtmlRendererInput))]
[JsonSerializable(typeof(HtmlRendererResult))]
[JsonSerializable(typeof(HtmlRendererRequest))]
[JsonSerializable(typeof(HtmlRendererResource))]
internal partial class HtmlRendererJson : JsonSerializerContext;

internal sealed class HtmlRendererProtocol(Stream input, Stream output)
{
    internal const int MaximumResourceBytes = 4 * 1024 * 1024;
    internal const int MaximumFrameBytes = 24 * 1024 * 1024;
    private const uint Magic = 0x4c4d4853;
    private const ushort Version = 1;
    private const int HeaderSize = 32;
    private readonly SemaphoreSlim _writer = new(1);
    private long _sent, _received;

    internal readonly record struct Frame(HtmlRendererMessage Type, long Document, int Correlation, byte[] Payload)
    {
        internal T Decode<T>(JsonTypeInfo<T> type) => JsonSerializer.Deserialize(Payload, type)
            ?? throw new InvalidDataException("Missing renderer message payload.");
    }

    internal async Task<Frame> ReadAsync(CancellationToken token)
    {
        var header = new byte[HeaderSize];
        await input.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var type = (HtmlRendererMessage)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        var sequence = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(12));
        var document = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(20));
        var correlation = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(28));
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)) != Version ||
            sequence != ++_received || document < 0 || correlation < 0 || !ValidLength(type, length))
            throw new InvalidDataException("Invalid renderer message header.");
        var payload = new byte[length];
        await input.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        return new(type, document, correlation, payload);
    }

    internal Task WriteAsync<T>(HtmlRendererMessage type, long document, int correlation, T value, JsonTypeInfo<T> json, CancellationToken token) =>
        WriteAsync(type, document, correlation, token, JsonSerializer.SerializeToUtf8Bytes(value, json));

    internal async Task WriteAsync(HtmlRendererMessage type, long document, int correlation, CancellationToken token, byte[]? payload = null)
    {
        var length = payload?.Length ?? 0;
        if (!ValidLength(type, length)) throw new InvalidDataException("Renderer message exceeds its budget.");
        await _writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var header = new byte[HeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), Version);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)type);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), length);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(12), ++_sent);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(20), document);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), correlation);
            await output.WriteAsync(header, token).ConfigureAwait(false);
            if (length != 0) await output.WriteAsync(payload!, token).ConfigureAwait(false);
            await output.FlushAsync(token).ConfigureAwait(false);
        }
        finally { _writer.Release(); }
    }

    private static bool ValidLength(HtmlRendererMessage type, int length) => type switch
    {
        HtmlRendererMessage.Hello or HtmlRendererMessage.Poll => length == 0,
        HtmlRendererMessage.Input => length is > 0 and <= 128 * 1024,
        HtmlRendererMessage.Request => length is > 0 and <= 512 * 1024,
        HtmlRendererMessage.Open or HtmlRendererMessage.Resource => length is > 0 and <= 6 * 1024 * 1024,
        HtmlRendererMessage.Result => length is > 0 and <= MaximumFrameBytes,
        _ => false
    };
}
