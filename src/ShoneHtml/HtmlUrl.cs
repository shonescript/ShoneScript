using System.Net;
using ShoneJs.Runtime;

namespace ShoneHtml;

internal sealed partial class HtmlScriptHost
{
    private static readonly string[] UrlParts = ["href", "origin", "protocol", "username", "password", "host", "hostname", "port", "pathname", "search", "hash"];
    private JsObject _paramsPrototype = null!;

    private void BindUrl()
    {
        _paramsPrototype = NewObject();
        Accessor(_paramsPrototype, "size", e => JsValue.From((double)Params(e).Pairs.Count));
        Method(_paramsPrototype, "append", 2, (e, a) => { var p = Params(e); p.Pairs.Add((Arg(a, 0).ToJsString(), Arg(a, 1).ToJsString())); p.Update(); return JsValue.Undefined; });
        Method(_paramsPrototype, "delete", 1, (e, a) =>
        {
            var p = Params(e); p.Pairs.RemoveAll(pair => pair.Name == Arg(a, 0).ToJsString() && (Arg(a, 1).Type == JsType.Undefined || pair.Value == Arg(a, 1).ToJsString())); p.Update(); return JsValue.Undefined;
        });
        Method(_paramsPrototype, "has", 1, (e, a) => JsValue.From(Params(e).Pairs.Any(pair => pair.Name == Arg(a, 0).ToJsString() && (Arg(a, 1).Type == JsType.Undefined || pair.Value == Arg(a, 1).ToJsString()))));
        Method(_paramsPrototype, "get", 1, (e, a) => NullableString(Params(e).Pairs.Find(pair => pair.Name == Arg(a, 0).ToJsString()).Value));
        Method(_paramsPrototype, "getAll", 1, (e, a) =>
        {
            var values = NewArray(); foreach (var pair in Params(e).Pairs) if (pair.Name == Arg(a, 0).ToJsString()) values.Add(JsValue.From(pair.Value)); return JsValue.From(values);
        });
        Method(_paramsPrototype, "set", 2, (e, a) =>
        {
            var p = Params(e); var name = Arg(a, 0).ToJsString(); var value = Arg(a, 1).ToJsString();
            var index = p.Pairs.FindIndex(pair => pair.Name == name);
            if (index < 0) p.Pairs.Add((name, value));
            else { p.Pairs[index] = (name, value); for (var i = p.Pairs.Count - 1; i > index; i--) if (p.Pairs[i].Name == name) p.Pairs.RemoveAt(i); }
            p.Update(); return JsValue.Undefined;
        });
        Method(_paramsPrototype, "sort", 0, (e, _) => { var p = Params(e); p.Replace(p.Pairs.OrderBy(pair => pair.Name, StringComparer.Ordinal).ToArray()); p.Update(); return JsValue.Undefined; });
        Method(_paramsPrototype, "toString", 0, (e, _) => JsValue.From(Params(e).Encode()));
        Method(_paramsPrototype, "forEach", 1, (e, a) =>
        {
            var p = Params(e);
            if (Arg(a, 0).Data is not JsFunction callback) throw TypeError("Expected a callback");
            foreach (var (name, value) in p.Pairs.ToArray()) callback.Call(Arg(a, 1), [JsValue.From(value), JsValue.From(name), e]);
            return JsValue.Undefined;
        });
        Constructor("URLSearchParams", 0, a => JsValue.From(CreateParams(Arg(a, 0))), _paramsPrototype);

        var prototype = NewObject();
        foreach (var part in UrlParts)
            Accessor(prototype, part, e => JsValue.From(UrlPart(AsUrl(e).Address, part)), part == "origin" ? null : (e, v) =>
            {
                var url = AsUrl(e);
                if (ChangeUrl(url.Address, part, v.ToJsString()) is { } address) url.SetAddress(address);
                else if (part == "href") throw TypeError("Invalid URL");
            });
        Accessor(prototype, "searchParams", e => JsValue.From(AsUrl(e).Params));
        Method(prototype, "toString", 0, (e, _) => JsValue.From(AsUrl(e).Address.AbsoluteUri));
        Method(prototype, "toJSON", 0, (e, _) => JsValue.From(AsUrl(e).Address.AbsoluteUri));
        Constructor("URL", 1, a =>
        {
            Uri? address = null;
            var input = Arg(a, 0).ToJsString();
            if (Arg(a, 1).Type == JsType.Undefined) Uri.TryCreate(input, UriKind.Absolute, out address);
            else if (Uri.TryCreate(a[1].ToJsString(), UriKind.Absolute, out var baseAddress)) Uri.TryCreate(baseAddress, input, out address);
            if (address is null) throw TypeError("Invalid URL");
            return JsValue.From(new UrlObject(address, _paramsPrototype) { Prototype = prototype });
        }, prototype);
    }

    private UrlParams CreateParams(JsValue input)
    {
        var result = new UrlParams { Prototype = _paramsPrototype };
        if (input.Data is UrlParams other) result.Pairs.AddRange(other.Pairs);
        else if (input.Data is JsArray array)
        {
            for (var i = 0; i < array.Length; i++)
            {
                if (array.Get(i.ToString(System.Globalization.CultureInfo.InvariantCulture)).Data is not JsArray { Length: 2 } pair) throw TypeError("Expected search parameter name/value pairs");
                result.Pairs.Add((pair.Get("0").ToJsString(), pair.Get("1").ToJsString()));
            }
        }
        else if (input.Data is JsObject record)
        {
            foreach (var name in record.OwnPropertyKeys()) if (record.GetOwnProperty(name)?.Enumerable == true) result.Pairs.Add((name, record.Get(name).ToJsString()));
        }
        else if (input.Type is not (JsType.Undefined or JsType.Null)) result.Replace(ParseQuery(input.ToJsString()));
        return result;
    }

    private static UrlObject AsUrl(JsValue value) => value.Data as UrlObject ?? throw TypeError("Expected a URL");
    private static UrlParams Params(JsValue value) => value.Data as UrlParams ?? throw TypeError("Expected URLSearchParams");
    private static string UrlPart(Uri url, string name) => name switch
    {
        "href" => url.AbsoluteUri, "origin" => url.Scheme is "http" or "https" or "ws" or "wss" or "ftp" ? url.GetLeftPart(UriPartial.Authority).Replace(url.UserInfo + "@", "", StringComparison.Ordinal) : "null",
        "protocol" => url.Scheme + ":", "username" => url.UserInfo.Split(':', 2)[0], "password" => url.UserInfo.Split(':', 2).ElementAtOrDefault(1) ?? "",
        "host" => url.IdnHost + (url.IsDefaultPort ? "" : ":" + url.Port), "hostname" => url.IdnHost,
        "port" => url.IsDefaultPort ? "" : url.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "pathname" => url.AbsolutePath, "search" => url.Query == "?" ? "" : url.Query, "hash" => url.Fragment == "#" ? "" : url.Fragment, _ => ""
    };

    private static Uri? ChangeUrl(Uri address, string part, string value)
    {
        try
        {
            if (part == "href") return Uri.TryCreate(value, UriKind.Absolute, out var url) ? url : null;
            var b = new UriBuilder(address);
            switch (part)
            {
                case "protocol": b.Scheme = value.TrimEnd(':'); break;
                case "host":
                    if (!Uri.TryCreate($"{b.Scheme}://{value}", UriKind.Absolute, out var other) || other.UserInfo.Length != 0 || other.AbsolutePath != "/" || other.Query.Length != 0 || other.Fragment.Length != 0) return null;
                    b.Host = other.Host; b.Port = other.IsDefaultPort ? -1 : other.Port; break;
                case "hostname": b.Host = value; break;
                case "username": b.UserName = value; break;
                case "password": b.Password = value; break;
                case "port":
                    if (value.Length == 0) b.Port = -1;
                    else if (ushort.TryParse(value, out var port)) b.Port = port;
                    else return null;
                    break;
                case "pathname": b.Path = value; break;
                case "search": b.Query = value; break;
                case "hash": b.Fragment = value; break;
                default: return null;
            }
            return b.Uri;
        }
        catch (Exception e) when (e is UriFormatException or ArgumentException) { return null; }
    }

    private static IEnumerable<(string Name, string Value)> ParseQuery(string query)
    {
        if (query.StartsWith('?')) query = query[1..];
        foreach (var entry in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = entry.IndexOf('=');
            yield return (WebUtility.UrlDecode(i < 0 ? entry : entry[..i]), i < 0 ? "" : WebUtility.UrlDecode(entry[(i + 1)..]));
        }
    }

    private static string EncodeQuery(string value) => Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal)
        .Replace("%2A", "*", StringComparison.Ordinal).Replace("~", "%7E", StringComparison.Ordinal);

    private sealed class UrlObject : JsObject
    {
        public UrlObject(Uri address, JsObject paramsPrototype)
        {
            Address = address;
            Params = new UrlParams(() => Address = new UriBuilder(Address) { Query = Params!.Encode() }.Uri) { Prototype = paramsPrototype };
            Params.Replace(ParseQuery(address.Query));
        }
        public Uri Address { get; private set; }
        public UrlParams Params { get; }
        public void SetAddress(Uri url) { Address = url; Params.Replace(ParseQuery(url.Query)); }
    }

    private sealed class UrlParams(Action? changed = null) : JsObject
    {
        public List<(string Name, string Value)> Pairs { get; } = [];
        public void Replace(IEnumerable<(string Name, string Value)> pairs) { Pairs.Clear(); Pairs.AddRange(pairs); }
        public void Update() => changed?.Invoke();
        public string Encode() => string.Join('&', Pairs.Select(p => EncodeQuery(p.Name) + "=" + EncodeQuery(p.Value)));
    }
}
