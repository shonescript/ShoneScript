using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ShoneHtml;

internal enum HtmlPublicRequestKind { Navigation, Style, Image, Script, Fetch }

internal sealed class HtmlPublicNetworkLimitException(string message) : HttpRequestException(message);

internal sealed class HtmlPublicNetwork : IDisposable
{
    private const int MaximumAddressLength = 16 * 1024;
    private const int MaximumHeaderBytes = 8 * 1024;
    private const int MaximumDnsAnswers = 64;
    private const int MaximumAddressesPerHop = 8;
    private static readonly HttpRequestOptionsKey<Budget> ScopeBudget = new("ShoneHtml.PublicNetworkBudget");
    private readonly HtmlBrowserOptions _options;
    private readonly HttpClient _client;

    public HtmlPublicNetwork(HtmlBrowserOptions options)
    {
        _options = options;
        // Do not let certificate chain building initiate its own network requests.
        var chainPolicy = new X509ChainPolicy { DisableCertificateDownloads = true, RevocationMode = X509RevocationMode.NoCheck };
        chainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1")); // TLS server authentication
        _client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            Credentials = null,
            PreAuthenticate = false,
            ActivityHeadersPropagator = null,
            AutomaticDecompression = DecompressionMethods.None,
            MaxResponseHeadersLength = 32,
            MaxResponseDrainSize = 0,
            ResponseDrainTimeout = TimeSpan.Zero,
            MaxConnectionsPerServer = 8,
            ConnectTimeout = options.RequestTimeout,
            PooledConnectionLifetime = TimeSpan.Zero,
            SslOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = chainPolicy },
            ConnectCallback = ConnectPublicAsync
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(HtmlBrowser.UserAgent);
        _client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity");
    }

    public Scope CreateScope() => new(this);
    public void Dispose() => _client.Dispose();

    internal sealed class Scope
    {
        private readonly HtmlPublicNetwork _network;
        private readonly Budget _budget;
        private readonly int _responseLimit;

        internal Scope(HtmlPublicNetwork network)
        {
            _network = network;
            var options = network._options;
            _responseLimit = Math.Min(options.MaximumResponseBytes, 8 * 1024 * 1024);
            _budget = new Budget(Math.Min(options.MaximumPageResources, 128), Math.Min(options.MaximumRedirects, 20),
                Math.Min(options.MaximumPageResourceBytes, 32 * 1024 * 1024));
        }

        public bool IsExhausted => _budget.IsExhausted;

        public async Task<HtmlResource> LoadAsync(Uri address, CancellationToken cancellationToken, HtmlPublicRequestKind kind,
            HttpMethod? method = null, byte[]? body = null, string? contentType = null,
            IReadOnlyDictionary<string, string>? headers = null, Func<Uri, Uri, bool>? allowRedirect = null,
            string? encodedFormQuery = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _budget.TakeRequest(kind);
            method ??= HttpMethod.Get;
            ValidateRequest(kind, method, body, contentType, headers, encodedFormQuery, _budget, _responseLimit);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_network._options.RequestTimeout);
            var token = deadline.Token;
            var redirected = false;
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    ValidateAddress(address, _budget);
                    var wireAddress = BuildWireAddress(address, encodedFormQuery);
                    if (wireAddress.AbsoluteUri.Length > MaximumAddressLength)
                        throw _budget.Exhaust("Public request address exceeds the configured limit.");
                    _budget.ChargeBytes(wireAddress.AbsoluteUri.Length + (body?.Length ?? 0L));
                    using var request = new HttpRequestMessage(method, wireAddress)
                    {
                        Version = HttpVersion.Version11,
                        VersionPolicy = HttpVersionPolicy.RequestVersionExact
                    };
                    request.Options.Set(ScopeBudget, _budget);
                    request.Headers.ConnectionClose = true;
                    if (body is not null)
                        request.Content = new ByteArrayContent(body);
                    if (contentType is not null)
                        request.Content!.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
                    if (headers is not null)
                    {
                        foreach (var (name, value) in headers)
                        {
                            if (name.StartsWith("content-", StringComparison.OrdinalIgnoreCase))
                                request.Content?.Headers.TryAddWithoutValidation(name, value);
                            else
                                request.Headers.TryAddWithoutValidation(name, value);
                            _budget.ChargeBytes(name.Length + value.Length + 4);
                        }
                    }
                    _budget.ChargeBytes(contentType?.Length ?? 0);

                    using var response = await _network._client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                        .ConfigureAwait(false);
                    var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var header in response.Headers.Concat(response.Content.Headers))
                    {
                        string value = string.Join(", ", header.Value);
                        _budget.ChargeBytes(header.Key.Length + value.Length + 4);
                        if (!header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) &&
                            !header.Key.Equals("Set-Cookie2", StringComparison.OrdinalIgnoreCase))
                            responseHeaders[header.Key] = value;
                    }
                    // 1xx, redirect and other discarded bodies are not drained. The socket is closed on disposal.
                    if (HtmlResources.IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
                    {
                        _budget.TakeRedirect();
                        var next = ResolveRedirect(address, location, allowRedirect, _budget);
                        if (!HtmlResources.SameOrigin(address, next) && kind == HtmlPublicRequestKind.Fetch)
                            throw new HttpRequestException("Cross-origin fetch redirects are not supported.");
                        if ((int)response.StatusCode == 303 && method != HttpMethod.Head ||
                            (int)response.StatusCode is 301 or 302 && method == HttpMethod.Post)
                        {
                            method = HttpMethod.Get;
                            body = null;
                            contentType = null;
                            headers = headers?.Where(pair => !pair.Key.StartsWith("content-", StringComparison.OrdinalIgnoreCase))
                                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                        }
                        address = next;
                        encodedFormQuery = null;
                        redirected = true;
                        continue;
                    }

                    var bytes = await ReadBodyAsync(response.Content, method, _responseLimit, _budget, token).ConfigureAwait(false);
                    return new HtmlResource(address, response.StatusCode, bytes,
                        response.Content.Headers.ContentType?.ToString(), responseHeaders, redirected);
                }
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                throw new HttpRequestException("HTTP request timed out.", exception);
            }
            catch (HttpRequestException exception) when (exception.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
            {
                throw _budget.Exhaust("Public HTTP transport limit exceeded.");
            }
            catch (Exception exception) when (_budget.IsExhausted &&
                (exception is IOException || exception is HttpRequestException and not HtmlPublicNetworkLimitException))
            {
                // ConnectCallback exceptions may be wrapped by SocketsHttpHandler.
                throw _budget.ExhaustedException();
            }
        }
    }

    // Keep the validated URL-encoded query byte-for-byte and never transmit a fragment.
    internal static Uri BuildWireAddress(Uri address, string? validatedFormQuery) => validatedFormQuery is { Length: > 0 }
        ? new Uri(address.GetLeftPart(UriPartial.Path) + "?" + validatedFormQuery,
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true })
        : HtmlResources.WithoutFragment(address);

    internal static Uri ResolveRedirect(Uri address, Uri location, Func<Uri, Uri, bool>? allowRedirect, Budget budget)
    {
        if (!Uri.TryCreate(address, location, out var next))
            throw new HttpRequestException("Redirect to an invalid address.");
        ValidateAddress(next, budget);
        if (allowRedirect is not null && !allowRedirect(address, next))
            throw new HttpRequestException("Redirect blocked by the request policy.");
        if (!location.OriginalString.Contains('#') && address.Fragment.Length > 0)
            next = new UriBuilder(next) { Fragment = address.Fragment }.Uri;
        return next;
    }

    private static void ValidateAddress(Uri address, Budget budget)
    {
        if (!HtmlResources.IsHttpAddress(address) || !address.IsDefaultPort || address.Port is not (80 or 443))
            throw new HttpRequestException("Public requests require HTTP(S) on its default port, without user information.");
        if (address.AbsoluteUri.Length > MaximumAddressLength)
            throw budget.Exhaust("Public request address exceeds the configured limit.");
        if (address.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            // Uri's IPv6 host property can omit the scope, unlike IdnHost.
            if (!IPAddress.TryParse(address.IdnHost, out var literal) || !IsPublicAddress(literal))
                throw new HttpRequestException("Public requests cannot connect to a non-public destination.");
        }
        else if (address.HostNameType != UriHostNameType.Dns || !IsPublicDnsName(address.IdnHost))
            throw new HttpRequestException("Public requests require a public DNS name or IP address.");
    }

    internal static bool IsPublicDnsName(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        if (host.Length > 253 || !host.Contains('.') || host.Split('.').Any(label =>
            label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-') ||
            label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            return false;
        return !(host.EndsWith(".localhost", StringComparison.Ordinal) || host.EndsWith(".local", StringComparison.Ordinal) ||
            host.EndsWith(".internal", StringComparison.Ordinal) || host.EndsWith(".home.arpa", StringComparison.Ordinal) ||
            host == "home.arpa" || host.EndsWith(".test", StringComparison.Ordinal) ||
            host.EndsWith(".invalid", StringComparison.Ordinal) || host.EndsWith(".example", StringComparison.Ordinal) ||
            host.EndsWith(".onion", StringComparison.Ordinal));
    }

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(ScopeBudget, out var budget))
            throw new HttpRequestException("Missing public request scope.");
        budget.TakeConnection();
        var endpoint = context.DnsEndPoint;
        var host = endpoint.Host;
        var candidates = IPAddress.TryParse(host, out var literal) ? [literal] :
            await ResolvePublicDnsAsync(host, token).ConfigureAwait(false);
        HashSet<IPAddress> localAddresses;
        try
        {
            localAddresses = NetworkInterface.GetAllNetworkInterfaces().SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Select(entry => Normalize(entry.Address)).ToHashSet();
        }
        catch (NetworkInformationException exception)
        {
            throw new HttpRequestException("Unable to validate the local network interfaces.", exception);
        }
        var addresses = SelectPublicAddresses(candidates, localAddresses);
        SocketException? lastError = null;
        foreach (var address in addresses)
        {
            token.ThrowIfCancellationRequested();
            budget.Check();
            var peer = new IPEndPoint(address, endpoint.Port);
            var socket = new Socket(peer.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                // Connect with the validated numeric IP; never ask the socket layer to resolve the host again.
                await socket.ConnectAsync(peer, token).ConfigureAwait(false);
                if (!peer.Equals(socket.RemoteEndPoint))
                    throw new HttpRequestException("The connected peer did not match the validated public endpoint.");
                return new AccountedNetworkStream(new NetworkStream(socket, ownsSocket: true), budget);
            }
            catch (SocketException exception)
            {
                socket.Dispose();
                lastError = exception;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new HttpRequestException("Connection to the validated public endpoint failed.", lastError);
    }

    // Account for framing, TLS, and bytes read ahead by the HTTP implementation separately from payloads.
    internal sealed class AccountedNetworkStream(Stream inner, Budget budget) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = inner.Read(buffer[..budget.GetWireReadSize(buffer.Length)]);
            budget.ChargeWireBytes(count);
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var count = await inner.ReadAsync(buffer[..budget.GetWireReadSize(buffer.Length)], token).ConfigureAwait(false);
            budget.ChargeWireBytes(count);
            return count;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            budget.ChargeWireBytes(buffer.Length);
            inner.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            WriteAsync(buffer.AsMemory(offset, count), token).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            budget.ChargeWireBytes(buffer.Length);
            return inner.WriteAsync(buffer, token);
        }
    }

    private static Task<IPAddress[]> ResolvePublicDnsAsync(string host, CancellationToken token)
    {
        if (!IsPublicDnsName(host))
            throw new HttpRequestException("Not a public DNS name.");
        // A fully qualified lookup prevents appending a host DNS search suffix.
        return Dns.GetHostAddressesAsync(host.TrimEnd('.') + ".", token);
    }

    internal static IPAddress[] SelectPublicAddresses(IPAddress[] answers, IReadOnlySet<IPAddress> localAddresses)
    {
        if (answers.Length is 0 or > MaximumDnsAnswers)
            throw new HttpRequestException("Unexpected public DNS answer count.");
        if (answers.Any(address => !IsPublicAddress(address) || localAddresses.Contains(Normalize(address))))
            throw new HttpRequestException("Public requests cannot connect to a non-public destination.");
        return answers.Select(Normalize).Distinct().Take(MaximumAddressesPerHop).ToArray();
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
            return false;
        address = Normalize(address);
        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out var length))
            return false;
        if (length == 4)
        {
            var a = bytes[0]; var b = bytes[1]; var c = bytes[2];
            return !(a is 0 or 10 or 127 or >= 224 || a == 100 && b is >= 64 and <= 127 ||
                a == 169 && b == 254 || a == 172 && b is >= 16 and <= 31 ||
                a == 192 && (b == 0 && c is 0 or 2 || b == 31 && c == 196 || b == 52 && c == 193 ||
                    b == 88 && c == 99 || b == 168 || b == 175 && c == 48) ||
                a == 198 && (b is 18 or 19 || b == 51 && c == 100) || a == 203 && b == 0 && c == 113);
        }
        // Only global unicast; exclude protocol-assignment, documentation and transition prefixes within it.
        return length == 16 && (bytes[0] & 0xe0) == 0x20 &&
            !(bytes[0] == 0x20 && (bytes[1] == 2 || bytes[1] == 1 &&
                (bytes[2] < 2 || bytes[2] == 0x0d && bytes[3] == 0xb8))) &&
            !(bytes[0] == 0x3f && (bytes[1] == 0xfe || bytes[1] == 0xff && bytes[2] < 0x10)) &&
            !(bytes[0] == 0x26 && bytes[1] == 0x20 && bytes[2] == 0 && bytes[3] == 0x4f && bytes[4] == 0x80 && bytes[5] == 0);
    }

    private static void ValidateRequest(HtmlPublicRequestKind kind, HttpMethod method, byte[]? body, string? contentType,
        IReadOnlyDictionary<string, string>? headers, string? formQuery, Budget budget, int responseLimit)
    {
        if (!(kind switch
        {
            HtmlPublicRequestKind.Navigation => method == HttpMethod.Get || method == HttpMethod.Post,
            HtmlPublicRequestKind.Style or HtmlPublicRequestKind.Image or HtmlPublicRequestKind.Script => method == HttpMethod.Get,
            HtmlPublicRequestKind.Fetch => method.Method is "GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE" or "OPTIONS",
            _ => false
        }) || body is not null && (method == HttpMethod.Get || method == HttpMethod.Head) || contentType is not null && body is null)
            throw new HttpRequestException("Invalid public request method or body.");
        if (body?.Length > Math.Min(responseLimit, 1024 * 1024))
            throw budget.Exhaust("Public request body exceeds the configured limit.");
        if (formQuery is not null)
        {
            if (formQuery.Length > MaximumAddressLength)
                throw budget.Exhaust("Public form query exceeds the configured limit.");
            if (kind != HtmlPublicRequestKind.Navigation || method != HttpMethod.Get || !IsEncodedFormQuery(formQuery))
                throw new HttpRequestException("Invalid public form query.");
        }
        if (headers is { Count: > 0 } && kind != HtmlPublicRequestKind.Fetch)
            throw new HttpRequestException("Only public fetch requests can carry additional headers.");
        var headerBytes = 0;
        if (contentType is not null)
        {
            ValidateValue(contentType, budget);
            if (!MediaTypeHeaderValue.TryParse(contentType, out var media) || kind == HtmlPublicRequestKind.Navigation &&
                !string.Equals(media.MediaType, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException("Invalid public request content type.");
            headerBytes = contentType.Length;
        }
        if (kind == HtmlPublicRequestKind.Navigation && method == HttpMethod.Post &&
            (body is null || contentType is null || !IsEncodedFormQuery(Encoding.ASCII.GetString(body))))
            throw new HttpRequestException("Public POST navigation requires URL-encoded form data.");
        if (headers is null)
            return;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            if (name.Length > 32 || !names.Add(name))
                throw new HttpRequestException("Invalid or duplicate public request header.");
            ValidateValue(value, budget);
            switch (name.ToLowerInvariant())
            {
                case "accept": case "accept-language": case "content-language":
                    break;
                case "content-type" when contentType is null && MediaTypeHeaderValue.TryParse(value, out _):
                    break;
                case "origin" when Uri.TryCreate(value, UriKind.Absolute, out var origin) && HtmlResources.IsHttpAddress(origin) &&
                    origin.GetLeftPart(UriPartial.Authority).Equals(value, StringComparison.Ordinal):
                    break;
                default:
                    throw new HttpRequestException("Unsupported public request header.");
            }
            headerBytes += name.Length + value.Length + 4;
            if (headerBytes > MaximumHeaderBytes)
                throw budget.Exhaust("Public request headers exceed the configured limit.");
        }
    }

    private static void ValidateValue(string value, Budget budget)
    {
        if (value.Length > MaximumHeaderBytes)
            throw budget.Exhaust("Public request headers exceed the configured limit.");
        if (value.Any(c => c < ' ' && c != '\t' || c >= 127))
            throw new HttpRequestException("Invalid public request header value.");
    }

    internal static bool IsEncodedFormQuery(string query)
    {
        for (var i = 0; i < query.Length; i++)
        {
            var c = query[i];
            if (char.IsAsciiLetterOrDigit(c) || "*-._~+&=".Contains(c))
                continue;
            if (c != '%' || i + 2 >= query.Length || !char.IsAsciiHexDigit(query[i + 1]) || !char.IsAsciiHexDigit(query[i + 2]))
                return false;
            i += 2;
        }
        return true;
    }

    internal static async Task<byte[]> ReadBodyAsync(HttpContent content, HttpMethod method, int limit, Budget budget, CancellationToken token)
    {
        if (method == HttpMethod.Head)
            return [];
        if (content.Headers.ContentEncoding.Any(encoding => !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new HttpRequestException("Unsupported public response content encoding.");
        if (content.Headers.ContentLength is { } declared)
        {
            if (declared > limit)
                throw budget.Exhaust("Public response body exceeds the configured limit.");
            budget.CheckAvailableBytes(declared);
        }
        using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var chunk = new byte[Math.Min(limit + 1, 8192)];
        while (true)
        {
            var size = budget.GetReadSize((int)Math.Min(chunk.Length, limit - output.Length + 1));
            var count = await stream.ReadAsync(chunk.AsMemory(0, size), token).ConfigureAwait(false);
            if (count == 0)
                return output.ToArray();
            budget.ChargeBytes(count);
            if (output.Length + count > limit)
                throw budget.Exhaust("Public response body exceeds the configured limit.");
            output.Write(chunk, 0, count);
        }
    }

    internal sealed class Budget(int resources, int redirects, long bytes)
    {
        private readonly object _gate = new();
        private int _resources = resources;
        private int _redirects = redirects;
        private int _connections = resources + redirects + 1;
        private long _bytes = bytes;
        private long _wireBytes = 2 * bytes + (resources + redirects + 1L) * 64 * 1024;
        private bool _navigation;
        private string? _exhaustion;

        public bool IsExhausted { get { lock (_gate) return _exhaustion is not null; } }
        public HtmlPublicNetworkLimitException ExhaustedException() { lock (_gate) return new(_exhaustion ?? "Public request scope exhausted."); }
        public HtmlPublicNetworkLimitException Exhaust(string message) { lock (_gate) return new(_exhaustion ??= message); }
        public void Check() { lock (_gate) if (_exhaustion is not null) throw ExhaustedException(); }

        public void TakeRequest(HtmlPublicRequestKind kind)
        {
            lock (_gate)
            {
                Check();
                if (kind == HtmlPublicRequestKind.Navigation)
                {
                    if (_navigation)
                        throw Exhaust("Public navigation limit exceeded.");
                    _navigation = true;
                }
                else if (_resources-- == 0)
                    throw Exhaust("Public page request limit exceeded.");
            }
        }
        public void TakeRedirect()
        {
            lock (_gate)
            {
                Check();
                if (_redirects-- == 0)
                    throw Exhaust("Public page redirect limit exceeded.");
            }
        }
        public void TakeConnection()
        {
            lock (_gate)
            {
                Check();
                if (_connections-- == 0)
                    throw Exhaust("Public page connection limit exceeded.");
            }
        }
        public void CheckAvailableBytes(long count)
        {
            lock (_gate)
            {
                Check();
                if (count > _bytes)
                    throw Exhaust("Public page byte limit exceeded.");
            }
        }
        public void ChargeBytes(long count)
        {
            lock (_gate)
            {
                CheckAvailableBytes(count);
                _bytes -= count;
            }
        }
        public int GetReadSize(int capacity)
        {
            lock (_gate)
            {
                Check();
                return (int)Math.Min(_bytes + 1, capacity);
            }
        }
        public int GetWireReadSize(int capacity)
        {
            lock (_gate)
            {
                Check();
                return (int)Math.Min(_wireBytes + 1, capacity);
            }
        }
        public void ChargeWireBytes(int count)
        {
            lock (_gate)
            {
                Check();
                if (count > _wireBytes)
                    throw Exhaust("Public network wire byte limit exceeded.");
                _wireBytes -= count;
            }
        }
    }
}
