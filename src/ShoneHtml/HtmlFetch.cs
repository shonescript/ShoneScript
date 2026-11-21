using System.Globalization;
using System.Net;
using System.Text;
using ShoneJs.Runtime;

namespace ShoneHtml;

internal sealed partial class HtmlScriptHost
{
    private JsObject _headersPrototype = null!, _requestPrototype = null!, _responsePrototype = null!, _signalPrototype = null!;
    private int _fetches;
    private static readonly HashSet<string> SafeResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "cache-control", "content-language", "content-length", "content-type", "expires", "last-modified", "pragma"
    };

    private void BindFetch()
    {
        if (_engine.GetGlobal("JSON").Data is JsObject json)
            foreach (var name in new[] { "parse", "stringify" }) if (json.Get(name).Data is JsFunction fn) _jsonMethods.Add(name, fn);
        _headersPrototype = NewObject();
        Method(_headersPrototype, "get", 1, (e, a) => NullableString(AsHeaders(e).Fields.GetValueOrDefault(HeaderName(Arg(a, 0)))));
        Method(_headersPrototype, "has", 1, (e, a) => JsValue.From(AsHeaders(e).Fields.ContainsKey(HeaderName(Arg(a, 0)))));
        Method(_headersPrototype, "append", 2, (e, a) => { AsHeaders(e).SetField(Arg(a, 0), Arg(a, 1), append: true); return JsValue.Undefined; });
        Method(_headersPrototype, "set", 2, (e, a) => { AsHeaders(e).SetField(Arg(a, 0), Arg(a, 1), append: false); return JsValue.Undefined; });
        Method(_headersPrototype, "delete", 1, (e, a) =>
        {
            var obj = AsHeaders(e); if (obj.Immutable) throw TypeError("Headers are immutable"); obj.Fields.Remove(HeaderName(Arg(a, 0))); return JsValue.Undefined;
        });
        Method(_headersPrototype, "forEach", 1, (e, a) =>
        {
            if (Arg(a, 0).Data is not JsFunction fn) throw TypeError("Expected a callback");
            foreach (var (name, value) in AsHeaders(e).Fields.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToArray()) fn.Call(Arg(a, 1), [JsValue.From(value), JsValue.From(name), e]);
            return JsValue.Undefined;
        });
        Constructor("Headers", 0, a => JsValue.From(CreateHeaders(Arg(a, 0))), _headersPrototype);

        BindAbort();

        _requestPrototype = NewObject();
        Accessor(_requestPrototype, "url", e => JsValue.From(AsRequest(e).Address.AbsoluteUri));
        Accessor(_requestPrototype, "method", e => JsValue.From(AsRequest(e).Method));
        Accessor(_requestPrototype, "mode", e => JsValue.From(AsRequest(e).Mode));
        Accessor(_requestPrototype, "redirect", e => JsValue.From(AsRequest(e).Redirect));
        Accessor(_requestPrototype, "credentials", _ => JsValue.From("omit"));
        Accessor(_requestPrototype, "signal", e => JsValue.From(AsRequest(e).Signal));
        BindBody(_requestPrototype);
        Constructor("Request", 1, a => JsValue.From(CreateRequest(Arg(a, 0), Arg(a, 1))), _requestPrototype);

        _responsePrototype = NewObject();
        Accessor(_responsePrototype, "url", e => JsValue.From(AsResponse(e).Url));
        Accessor(_responsePrototype, "status", e => JsValue.From((double)AsResponse(e).Status));
        Accessor(_responsePrototype, "statusText", e => JsValue.From(AsResponse(e).StatusText));
        Accessor(_responsePrototype, "ok", e => JsValue.From(AsResponse(e).Status is >= 200 and <= 299));
        Accessor(_responsePrototype, "redirected", e => JsValue.From(AsResponse(e).Redirected));
        Accessor(_responsePrototype, "type", e => JsValue.From(AsResponse(e).Type));
        BindBody(_responsePrototype);
        Constructor("Response", 0, a =>
        {
            var data = ReadBody(Arg(a, 0));
            var init = Arg(a, 1).Data as JsObject;
            var status = (int)(init?.Get("status") is { Type: not JsType.Undefined } value ? value.ToNumber() : 200);
            if (status is < 200 or > 599) throw new JsException(Error("RangeError", "Invalid response status"));
            if (data.Text is not null && status is 204 or 205 or 304) throw TypeError("Response status cannot have a body");
            var statusText = init?.Get("statusText") is { Type: not JsType.Undefined } text ? text.ToJsString() : "";
            if (statusText.IndexOfAny(['\r', '\n', '\0']) >= 0) throw TypeError("Invalid HTTP status text");
            var headers = CreateHeaders(init?.Get("headers") ?? JsValue.Undefined);
            if (data.Type is { } media) headers.Fields.TryAdd("content-type", media);
            return JsValue.From(new ResponseObject(data.Text, headers, "", status, statusText, false, "default") { Prototype = _responsePrototype });
        }, _responsePrototype);

        Global("fetch", 1, (receiver, args) =>
        {
            var promise = _engine.CreatePromise(out var resolve, out var reject);
            try
            {
                var request = CreateRequest(Arg(args, 0), Arg(args, 1));
                if (request.Signal.Canceled) { reject(request.Signal.Reason); return promise; }
                var renderer = Renderer;
                var browser = _page.Browser;
                var origin = renderer?.Origin ?? _page.Address;
                if ((renderer is null && browser is null) || origin is null) throw TypeError("Fetch requires a browser document with an HTTP(S) origin");
                var cross = !HtmlResources.SameOrigin(origin, request.Address);
                var headers = new Dictionary<string, string>(request.Headers.Fields, StringComparer.OrdinalIgnoreCase);
                if (cross && (request.Mode == "same-origin" || !IsSimpleRequest(request.Method, headers))) throw TypeError("This cross-origin request would require unsupported CORS preflight");
                if (renderer is null && cross) headers["origin"] = UrlPart(origin, "origin");
                if (renderer is not null) ValidateRendererFetch(request, headers);
                if (Interlocked.Increment(ref _fetches) > 64)
                {
                    Interlocked.Decrement(ref _fetches);
                    throw BudgetError("Page fetch limit exceeded");
                }
                if (Arg(args, 0).Data is RequestObject original && request.Text is not null) original.Used = true;
                _ = FetchAsync(browser, renderer, origin, request, headers, cross, resolve, reject);
            }
            catch (JsException error) { reject(error.Value); }
            return promise;
        });
    }

    private void BindBody(JsObject prototype)
    {
        Accessor(prototype, "headers", e => JsValue.From(AsBody(e).Headers));
        Accessor(prototype, "bodyUsed", e => JsValue.From(AsBody(e).Used));
        Method(prototype, "text", 0, (e, _) => Consume(AsBody(e), json: false));
        Method(prototype, "json", 0, (e, _) => Consume(AsBody(e), json: true));
        Method(prototype, "clone", 0, (e, _) =>
        {
            var body = AsBody(e);
            if (body.Used) throw TypeError("Body has already been consumed");
            return body is RequestObject ? JsValue.From(CreateRequest(e, JsValue.Undefined)) : JsValue.From(AsResponse(e).Clone());
        });
    }

    private JsValue Consume(BodyObject body, bool json)
    {
        var promise = _engine.CreatePromise(out var resolve, out var reject);
        if (body.Used) reject(Error("TypeError", "Body has already been consumed"));
        else
        {
            if (body.Text is not null) body.Used = true;
            try { resolve(json ? Json("parse", JsValue.From(body.Text ?? "")) : JsValue.From(body.Text ?? "")); }
            catch (JsException error) { reject(error.Value); }
        }
        return promise;
    }

    private static void ValidateRendererFetch(RequestObject request, Dictionary<string, string> headers)
    {
        if (request.Address.AbsoluteUri.Length > 8192 || request.Method is not ("GET" or "HEAD" or "POST" or "PUT" or "DELETE" or "PATCH" or "OPTIONS") ||
            request.Text is { } text && (text.Length > 256 * 1024 || Encoding.UTF8.GetByteCount(text) > 256 * 1024) || headers.Count > 32)
            throw TypeError("Fetch exceeds the public request limits");
    }

    private async Task FetchAsync(HtmlBrowser? browser, HtmlRendererDocument? renderer, Uri origin, RequestObject request,
        Dictionary<string, string> headers, bool cross, Action<JsValue> resolve, Action<JsValue> reject)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_page.Lifetime, request.Signal.Token);
        try
        {
            var body = request.Text is null ? null : Encoding.UTF8.GetBytes(request.Text);
            var response = renderer is not null
                ? await renderer.LoadAsync(new(HtmlPublicRequestKind.Fetch, request.Address.AbsoluteUri, request.Method, body, headers,
                    request.Mode, request.Redirect), linked.Token).ConfigureAwait(false)
                : await browser!.Resources.LoadAsync(request.Address, linked.Token, new HttpMethod(request.Method), body, headers: headers,
                    allowRedirect: (_, next) => !cross && request.Redirect == "follow" && HtmlResources.SameOrigin(origin, next)).ConfigureAwait(false);
            if (renderer is not null && (!HtmlResources.SameOrigin(request.Address, response.Address) ||
                response.Redirected && (cross || request.Redirect == "error"))) throw new HttpRequestException("Redirect blocked by fetch policy");
            bool opaque = cross && request.Mode == "no-cors";
            var permitted = !cross || opaque || response.Headers.TryGetValue("Access-Control-Allow-Origin", out var allow) &&
                (allow == "*" || allow == (renderer is null ? UrlPart(origin, "origin") : origin.GetLeftPart(UriPartial.Authority)));
            if (!permitted) throw new HttpRequestException("Response is not exposed by CORS policy");
            var exposed = cross && response.Headers.TryGetValue("Access-Control-Expose-Headers", out var names)
                ? names.Split(',').Select(n => n.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
            var data = opaque || request.Method == "HEAD" || (int)response.Status is 204 or 205 or 304 ? null : response.DecodeUtf8();
            Post(() =>
            {
                var outputHeaders = new HeadersObject { Prototype = _headersPrototype, Immutable = true };
                if (!opaque)
                    foreach (var (name, value) in response.Headers)
                        if (!IsCookieHeader(name) && (!cross || SafeResponseHeaders.Contains(name) || exposed?.Contains(name) == true || exposed?.Contains("*") == true))
                            outputHeaders.Fields[name.ToLowerInvariant()] = value;
                resolve(JsValue.From(new ResponseObject(data, outputHeaders, opaque ? "" : HtmlResources.WithoutFragment(response.Address).AbsoluteUri,
                    opaque ? 0 : (int)response.Status, opaque ? "" : StatusText(response.Status), !opaque && response.Redirected,
                    opaque ? "opaque" : cross ? "cors" : "basic") { Prototype = _responsePrototype }));
            });
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { Post(() => reject(request.Signal.Reason)); }
        catch (Exception error) when (error is HttpRequestException or IOException or ArgumentException or InvalidOperationException)
        {
            Post(() => reject(Error("TypeError", error.Message)));
        }

        void Post(Action settle)
        {
            if (!PostTask("fetch", () =>
            {
                Interlocked.Decrement(ref _fetches);
                if (request.Signal.Canceled) reject(request.Signal.Reason); else settle();
            })) Interlocked.Decrement(ref _fetches);
        }
    }

    private RequestObject CreateRequest(JsValue input, JsValue options)
    {
        var existing = input.Data as RequestObject;
        if (existing?.Used == true) throw TypeError("Request body has already been consumed");
        var init = options.Data as JsObject;
        JsValue Read(string name) => init?.Get(name) ?? JsValue.Undefined;
        string ReadText(string name, string fallback) => Read(name) is { Type: not JsType.Undefined } v ? v.ToJsString() : fallback;
        var address = existing?.Address ?? (input.Data is UrlObject url ? url.Address : _page.ResolveAddress(input.ToJsString()));
        if (!HtmlResources.IsHttpAddress(address)) throw TypeError("Invalid request URL");
        var method = ReadText("method", existing?.Method ?? "GET");
        var upper = method.ToUpperInvariant();
        if (!IsToken(method) || upper is "CONNECT" or "TRACE" or "TRACK") throw TypeError("Invalid or forbidden HTTP method");
        if (upper is "DELETE" or "GET" or "HEAD" or "OPTIONS" or "POST" or "PUT" || Renderer is not null && upper == "PATCH") method = upper;
        var mode = ReadText("mode", existing?.Mode ?? "cors");
        var redirect = ReadText("redirect", existing?.Redirect ?? "follow");
        if (mode is not ("cors" or "same-origin" or "no-cors") || redirect is not ("follow" or "error")) throw TypeError("Unsupported fetch mode or redirect policy");
        if (ReadText("credentials", "omit") is not ("omit" or "same-origin")) throw TypeError("Credentialed cross-origin fetch is not supported");
        var headers = CreateHeaders(Read("headers") is { Type: not JsType.Undefined } explicitHeaders ? explicitHeaders :
            existing is null ? JsValue.Undefined : JsValue.From(existing.Headers));
        (string? Text, string? Type) body = Read("body") is { Type: not (JsType.Undefined or JsType.Null) } value ? ReadBody(value) : (existing?.Text, null);
        if (body.Text is not null && method is "GET" or "HEAD") throw TypeError("GET and HEAD requests cannot have a body");
        if (body.Type is { } media) headers.Fields.TryAdd("content-type", media);
        foreach (var name in headers.Fields.Keys.Where(IsForbiddenRequestHeader).ToArray()) headers.Fields.Remove(name);
        if (mode == "no-cors" && !IsSimpleRequest(method, headers.Fields)) throw TypeError("Request is not permitted in no-cors mode");
        var signal = Read("signal") is { Type: not (JsType.Undefined or JsType.Null) } signalValue ? AsSignal(signalValue) : existing?.Signal ?? NewSignal();
        return new RequestObject(body.Text, headers, address!, method, mode, redirect, signal) { Prototype = _requestPrototype };
    }

    private HeadersObject CreateHeaders(JsValue input)
    {
        var result = new HeadersObject { Prototype = _headersPrototype };
        if (input.Data is HeadersObject existing)
        {
            foreach (var (name, value) in existing.Fields) result.Fields.Add(name, value);
        }
        else if (input.Data is JsArray array)
        {
            for (var i = 0; i < array.Length; i++)
            {
                if (array.Get(i.ToString(CultureInfo.InvariantCulture)).Data is not JsArray { Length: 2 } pair) throw TypeError("Expected header name/value pairs");
                result.SetField(pair.Get("0"), pair.Get("1"), append: true);
            }
        }
        else if (input.Data is JsObject record)
        {
            foreach (var name in record.OwnPropertyKeys()) if (record.GetOwnProperty(name)?.Enumerable == true) result.SetField(JsValue.From(name), record.Get(name), append: true);
        }
        else if (input.Type is not (JsType.Undefined or JsType.Null)) throw TypeError("Invalid headers initializer");
        return result;
    }

    private static (string? Text, string? Type) ReadBody(JsValue value) => value.Data is UrlParams p ? (p.Encode(), "application/x-www-form-urlencoded;charset=UTF-8")
        : value.Type is JsType.Undefined or JsType.Null ? (null, null) : (value.ToJsString(), "text/plain;charset=UTF-8");

    private static bool IsSimpleRequest(string method, IReadOnlyDictionary<string, string> headers) => (method is "GET" or "HEAD" or "POST") &&
        headers.All(header => header.Value.Length <= 128 && header.Key switch
        {
            "accept" => !header.Value.Any(IsCorsUnsafeByte),
            "accept-language" or "content-language" => header.Value.All(c => char.IsAsciiLetterOrDigit(c) || " *,-.;=".Contains(c)),
            "content-type" => !header.Value.Any(IsCorsUnsafeByte) && header.Value.Split(';', 2)[0].Trim().ToLowerInvariant() is
                "application/x-www-form-urlencoded" or "multipart/form-data" or "text/plain",
            _ => false
        });

    private static bool IsCorsUnsafeByte(char c) => c < ' ' && c != '\t' || c == 127 || "\"():<>?@[\\]{}".Contains(c);
    private static bool IsCookieHeader(string name) => name.Equals("set-cookie", StringComparison.OrdinalIgnoreCase) || name.Equals("set-cookie2", StringComparison.OrdinalIgnoreCase);
    private static bool IsForbiddenRequestHeader(string name) => name.StartsWith("sec-", StringComparison.Ordinal) || name.StartsWith("proxy-", StringComparison.Ordinal) || name is
        "accept-charset" or "accept-encoding" or "access-control-request-headers" or "access-control-request-method" or "connection" or
        "content-length" or "cookie" or "cookie2" or "date" or "dnt" or "expect" or "host" or "keep-alive" or "origin" or "referer" or
        "set-cookie" or "te" or "trailer" or "transfer-encoding" or "upgrade" or "via" or "x-http-method" or "x-http-method-override" or "x-method-override";
    private static string StatusText(HttpStatusCode code) => code switch
    {
        HttpStatusCode.OK => "OK", HttpStatusCode.NotFound => "Not Found", HttpStatusCode.BadRequest => "Bad Request", HttpStatusCode.NoContent => "No Content",
        HttpStatusCode.InternalServerError => "Internal Server Error", _ => code.ToString()
    };
    private static bool IsToken(string value) => value.Length != 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c));
    private static string HeaderName(JsValue name) => name.ToJsString() is var text && IsToken(text) ? text.ToLowerInvariant() : throw TypeError("Invalid HTTP header name");
    private static HeadersObject AsHeaders(JsValue value) => value.Data as HeadersObject ?? throw TypeError("Expected Headers");
    private static BodyObject AsBody(JsValue value) => value.Data as BodyObject ?? throw TypeError("Expected a request or response");
    private static RequestObject AsRequest(JsValue value) => value.Data as RequestObject ?? throw TypeError("Expected a Request");
    private static ResponseObject AsResponse(JsValue value) => value.Data as ResponseObject ?? throw TypeError("Expected a Response");
    private static AbortController AsController(JsValue value) => value.Data as AbortController ?? throw TypeError("Expected an AbortController");
    private static AbortSignal AsSignal(JsValue value) => value.Data as AbortSignal ?? throw TypeError("Expected an AbortSignal");
    private AbortSignal NewSignal() => new() { Prototype = _signalPrototype };

    private sealed class HeadersObject : JsObject
    {
        public Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Immutable { get; init; }
        public void SetField(JsValue rawName, JsValue rawValue, bool append)
        {
            if (Immutable) throw TypeError("Headers are immutable");
            var name = HeaderName(rawName);
            var value = rawValue.ToJsString().Trim(' ', '\t', '\r', '\n');
            if (value.Any(c => c > 255 || c < ' ' && c != '\t' || c == 127)) throw TypeError("Invalid HTTP header value");
            if (Fields.Count >= 256 && !Fields.ContainsKey(name) || value.Length > 8192) throw TypeError("HTTP header limit exceeded");
            if (append && Fields.TryGetValue(name, out var previous))
            {
                if (previous.Length + value.Length + 2 > 8192) throw TypeError("HTTP header limit exceeded");
                value = previous + ", " + value;
            }
            Fields[name] = value;
        }
    }

    private abstract class BodyObject(string? text, HeadersObject headers) : JsObject
    {
        public string? Text { get; } = text;
        public HeadersObject Headers { get; } = headers;
        public bool Used;
    }
    private sealed class RequestObject(string? text, HeadersObject headers, Uri address, string method, string mode, string redirect, AbortSignal signal) : BodyObject(text, headers)
    {
        public Uri Address { get; } = address;
        public string Method { get; } = method;
        public string Mode { get; } = mode;
        public string Redirect { get; } = redirect;
        public AbortSignal Signal { get; } = signal;
    }
    private sealed class ResponseObject(string? text, HeadersObject headers, string url, int status, string statusText, bool redirected, string type) : BodyObject(text, headers)
    {
        public string Url { get; } = url;
        public int Status { get; } = status;
        public string StatusText { get; } = statusText;
        public bool Redirected { get; } = redirected;
        public string Type { get; } = type;
        public ResponseObject Clone()
        {
            var copy = new HeadersObject { Prototype = Headers.Prototype, Immutable = Headers.Immutable };
            foreach (var (name, value) in Headers.Fields) copy.Fields.Add(name, value);
            return new ResponseObject(Text, copy, Url, Status, StatusText, Redirected, Type) { Prototype = Prototype };
        }
    }
    private sealed class AbortController(AbortSignal signal) : JsObject { public AbortSignal Signal { get; } = signal; }
    private sealed class AbortSignal : JsObject
    {
        private CancellationTokenSource? _source;
        public CancellationToken Token => (_source ??= new CancellationTokenSource()).Token;
        public bool Canceled => _source?.IsCancellationRequested == true;
        public JsValue Reason = JsValue.Undefined;
        public AbortSignal[]? Sources;
        public List<WeakReference<AbortSignal>>? Dependents;
        public void Cancel(JsValue reason)
        {
            Reason = reason;
            (_source ??= new CancellationTokenSource()).Cancel();
        }
    }
}
