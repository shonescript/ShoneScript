using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace ShoneHtml;

internal sealed partial class HtmlResources : IDisposable
{
    private readonly HttpClient _client;
    private readonly HtmlBrowserOptions _options;

    public HtmlResources(HtmlBrowserOptions options)
    {
        _options = options;
        _client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            MaxResponseHeadersLength = 32,
            MaxConnectionsPerServer = 8,
            ConnectTimeout = options.RequestTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(HtmlBrowser.UserAgent);
    }

    internal static bool IsHttpAddress(Uri? address) =>
        address is { IsAbsoluteUri: true, UserInfo.Length: 0, Host.Length: > 0 } &&
        address.Scheme is "http" or "https";

    internal static bool SameOrigin(Uri a, Uri b) =>
        a.Scheme == b.Scheme && a.IdnHost.Equals(b.IdnHost, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    internal static Uri WithoutFragment(Uri address) => address.Fragment.Length == 0
        ? address : new UriBuilder(address) { Fragment = "" }.Uri;

    public async Task<HtmlResource> LoadAsync(Uri address, CancellationToken cancellationToken,
        HttpMethod? method = null, byte[]? body = null, string? contentType = null,
        IReadOnlyDictionary<string, string>? headers = null, Func<Uri, Uri, bool>? allowRedirect = null,
        string? encodedFormQuery = null)
    {
        if (!IsHttpAddress(address))
            throw new HttpRequestException("Only HTTP(S) addresses without user information are supported.");
        if (body?.Length > _options.MaximumResponseBytes)
            throw new HttpRequestException("Request body exceeds the configured limit.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.RequestTimeout);
        var token = deadline.Token;
        method ??= HttpMethod.Get;
        try
        {
            for (var redirects = 0; ; redirects++)
            {
                // The URL path has already been normalized. The form encoder emits only ASCII URL-safe bytes and escapes.
                var wireAddress = encodedFormQuery is { Length: > 0 }
                    ? new Uri(address.GetLeftPart(UriPartial.Path) + "?" + encodedFormQuery,
                        new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true })
                    : WithoutFragment(address);
                using var request = new HttpRequestMessage(method, wireAddress);
                if (body is not null)
                    request.Content = new ByteArrayContent(body);
                if (contentType is not null && request.Content is not null)
                    request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
                if (headers is not null)
                {
                    foreach (var (name, value) in headers)
                    {
                        if (!request.Headers.TryAddWithoutValidation(name, value) && request.Content is { } content)
                            content.Headers.TryAddWithoutValidation(name, value);
                    }
                }

                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
                if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
                {
                    if (redirects >= _options.MaximumRedirects)
                        throw new HttpRequestException("Too many redirects.");
                    if (!Uri.TryCreate(address, location, out var next) || !IsHttpAddress(next))
                        throw new HttpRequestException("Redirect to an unsupported address.");
                    if (allowRedirect is not null && !allowRedirect(address, next))
                        throw new HttpRequestException("Redirect blocked by the request policy.");
                    if (!SameOrigin(address, next))
                        headers = headers?.Where(pair => !IsCredentialHeader(pair.Key))
                            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                    if (!location.OriginalString.Contains('#') && address.Fragment.Length > 0)
                        next = new UriBuilder(next) { Fragment = address.Fragment }.Uri;
                    if ((int)response.StatusCode == 303 && method != HttpMethod.Head ||
                        (int)response.StatusCode is 301 or 302 && method == HttpMethod.Post)
                    {
                        method = HttpMethod.Get;
                        body = null;
                        contentType = null;
                        headers = headers?.Where(pair => !pair.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                    }
                    address = next;
                    encodedFormQuery = null;
                    continue;
                }

                var bytes = await ReadBodyAsync(response.Content, token).ConfigureAwait(false);
                var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var header in response.Headers.Concat(response.Content.Headers))
                    responseHeaders[header.Key] = string.Join(", ", header.Value);
                return new HtmlResource(address, response.StatusCode, bytes,
                    response.Content.Headers.ContentType?.ToString(), responseHeaders, redirects > 0);
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new HttpRequestException("HTTP request timed out.", exception);
        }
    }

    private async Task<byte[]> ReadBodyAsync(HttpContent content, CancellationToken token)
    {
        var limit = _options.MaximumResponseBytes;
        if (content.Headers.ContentLength > limit)
            throw new HttpRequestException("Response body exceeds the configured limit.");
        using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[Math.Min(limit, 81920)];
        while (true)
        {
            var count = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (count == 0)
                return buffer.ToArray();
            if (buffer.Length + count > limit)
                throw new HttpRequestException("Response body exceeds the configured limit.");
            buffer.Write(chunk, 0, count);
        }
    }

    internal static bool IsRedirect(HttpStatusCode status) => (int)status is 301 or 302 or 303 or 307 or 308;

    private static bool IsCredentialHeader(string name) =>
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Cookie", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _client.Dispose();
}

internal sealed partial record HtmlResource(Uri Address, HttpStatusCode Status, byte[] Bytes, string? ContentType,
    IReadOnlyDictionary<string, string> Headers, bool Redirected)
{
    public bool IsSuccess => (int)Status is >= 200 and < 300;

    public string DecodeText(bool html = false)
    {
        if (Bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            return Encoding.UTF8.GetString(Bytes, 3, Bytes.Length - 3);
        if (Bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble))
            return Encoding.Unicode.GetString(Bytes, 2, Bytes.Length - 2);
        if (Bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble))
            return Encoding.BigEndianUnicode.GetString(Bytes, 2, Bytes.Length - 2);

        var label = MediaTypeHeaderValue.TryParse(ContentType, out var media) ? media.CharSet : null;
        var encoding = GetEncoding(label);
        if (encoding is null && html)
        {
            var prefix = Encoding.Latin1.GetString(Bytes, 0, Math.Min(Bytes.Length, 1024));
            var match = MetaCharset().Match(prefix);
            encoding = match.Success ? GetEncoding(match.Groups[1].Value) : null;
            if (encoding?.CodePage is 1200 or 1201)
                encoding = Encoding.UTF8;
        }
        return (encoding ?? Encoding.UTF8).GetString(Bytes);
    }

    public string DecodeUtf8() => Bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)
        ? Encoding.UTF8.GetString(Bytes, 3, Bytes.Length - 3) : Encoding.UTF8.GetString(Bytes);

    private static Encoding? GetEncoding(string? label)
    {
        label = label?.Trim(' ', '\t', '\r', '\n', '\'', '"').ToLowerInvariant();
        if (string.IsNullOrEmpty(label))
            return null;
        if (label is "latin1" or "latin-1" or "iso-8859-1" or "us-ascii" or "ascii")
            label = "windows-1252";
        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(label) ?? Encoding.GetEncoding(label);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    [GeneratedRegex("<meta\\s+[^>]*?\\bcharset\\s*=\\s*['\"\\s]*([^'\"\\s;/>]+)[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetaCharset();
}
