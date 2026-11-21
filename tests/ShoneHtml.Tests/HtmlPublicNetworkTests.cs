using System.Net;
using System.Text;

namespace ShoneHtml.Tests;

public sealed class HtmlPublicNetworkTests
{
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.31.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("192.168.0.1")]
    [InlineData("198.19.0.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:8.8.8.8%1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2001::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002:7f00:1::")]
    [InlineData("2606:4700::1%1")]
    [InlineData("3fff::1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1%3")]
    [InlineData("ff02::1")]
    public void ReservedAddressesAreNotPublic(string address) =>
        Assert.False(HtmlPublicNetwork.IsPublicAddress(IPAddress.Parse(address)));

    [Fact]
    public void DnsAnswersAreCheckedFreshAsACompleteSetAndMappedAddressesArePinned()
    {
        var v4 = IPAddress.Parse("8.8.8.8");
        var v6 = IPAddress.Parse("2606:4700::1");
        var answer = new[] { v4.MapToIPv6(), v6, v4 };
        var localAddresses = new HashSet<IPAddress>();
        Assert.Equal(new[] { v4, v6 }, HtmlPublicNetwork.SelectPublicAddresses(answer, localAddresses));
        Assert.Throws<HttpRequestException>(() => HtmlPublicNetwork.SelectPublicAddresses([v4, IPAddress.Loopback], localAddresses));
        Assert.Throws<HttpRequestException>(() => HtmlPublicNetwork.SelectPublicAddresses([IPAddress.Parse("::ffff:10.0.0.1")], localAddresses));
        Assert.Throws<HttpRequestException>(() => HtmlPublicNetwork.SelectPublicAddresses([], localAddresses));
        Assert.Throws<HttpRequestException>(() => HtmlPublicNetwork.SelectPublicAddresses(answer, new HashSet<IPAddress> { v4 }));
        Assert.Throws<HttpRequestException>(() => HtmlPublicNetwork.SelectPublicAddresses(
            Enumerable.Repeat(v4, 8).Append(IPAddress.Loopback).ToArray(), localAddresses));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("service.localhost.")]
    [InlineData("service.local.")]
    [InlineData("service.internal")]
    [InlineData("service.home.arpa")]
    [InlineData("intranet")]
    [InlineData("service..domain")]
    [InlineData("service.invalid")]
    public void LocalAndSpecialNamesAreNotRequested(string name) => Assert.False(HtmlPublicNetwork.IsPublicDnsName(name));

    [Theory]
    [InlineData("http://127.1/")]
    [InlineData("http://2130706433/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://169.254.169.254/")]
    [InlineData("http://printer.local/")]
    [InlineData("http://user:pass@8.8.8.8/")]
    [InlineData("http://8.8.8.8:81/")]
    [InlineData("file:///tmp/document.html")]
    public async Task PublicNavigationRejectsUnsupportedDestinationsWithoutDialing(string address)
    {
        using var network = new HtmlPublicNetwork(new());
        await Assert.ThrowsAsync<HttpRequestException>(() => network.CreateScope().LoadAsync(
            new Uri(address), CancellationToken.None, HtmlPublicRequestKind.Navigation));
    }

    [Fact]
    public void RedirectPolicyChecksTheNewOriginAndAddress()
    {
        var start = new Uri("https://www.example.com/old#part");
        var budget = new HtmlPublicNetwork.Budget(2, 2, 1_000);
        var next = HtmlPublicNetwork.ResolveRedirect(start, new Uri("/new", UriKind.Relative), HtmlResources.SameOrigin, budget);
        Assert.Equal("https://www.example.com/new#part", next.AbsoluteUri);
        Assert.Throws<HttpRequestException>(() => HtmlPublicNetwork.ResolveRedirect(start,
            new Uri("//127.1/path", UriKind.Relative), null, budget));
        Assert.Throws<HttpRequestException>(() => HtmlPublicNetwork.ResolveRedirect(start,
            new Uri("https://other.example.com/page"), HtmlResources.SameOrigin, budget));
    }

    [Theory]
    [InlineData("Host", "www.example.com")]
    [InlineData("Authorization", "Basic Zm9vOmJhcg==")]
    [InlineData("Proxy-Authorization", "Basic Zm9vOmJhcg==")]
    [InlineData("Cookie", "a=b")]
    [InlineData("Transfer-Encoding", "chunked")]
    [InlineData("Origin", "http://www.example.com/path")]
    [InlineData("Accept", "x\r\nTest: value")]
    public async Task FetchRejectsUnsafeHeadersBeforeProcessingTheDestination(string name, string value)
    {
        using var network = new HtmlPublicNetwork(new());
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => network.CreateScope().LoadAsync(
            new Uri("http://127.0.0.1/"), CancellationToken.None, HtmlPublicRequestKind.Fetch,
            headers: new Dictionary<string, string> { [name] = value }));
        Assert.Contains("header", error.Message);
    }

    [Fact]
    public async Task HostOriginIsAcceptedAndPublicFormsAreValidatedBeforeDispatch()
    {
        using var network = new HtmlPublicNetwork(new());
        var loopback = new Uri("http://127.0.0.1/");
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => network.CreateScope().LoadAsync(
            loopback, CancellationToken.None, HtmlPublicRequestKind.Fetch,
            headers: new Dictionary<string, string> { ["Origin"] = "https://www.example.com" }));
        Assert.Contains("non-public destination", error.Message);
        const string query = "q=a+b%7E&submit=%E2%82%AC";
        Assert.True(HtmlPublicNetwork.IsEncodedFormQuery(query));
        Assert.Equal("https://www.example.com/form?" + query, HtmlPublicNetwork.BuildWireAddress(
            new Uri("https://www.example.com/form?replace=1#fragment"), query).AbsoluteUri);
        Assert.False(HtmlPublicNetwork.IsEncodedFormQuery("x=1#fragment"));
        Assert.False(HtmlPublicNetwork.IsEncodedFormQuery("x=%1"));
        error = await Assert.ThrowsAsync<HttpRequestException>(() => network.CreateScope().LoadAsync(
            loopback, CancellationToken.None, HtmlPublicRequestKind.Navigation, HttpMethod.Post,
            Encoding.ASCII.GetBytes("x\r\n=y"), "application/x-www-form-urlencoded"));
        Assert.Contains("form data", error.Message);
    }

    [Fact]
    public async Task FailedRequestsSpendTheSharedScopeAllowance()
    {
        using var network = new HtmlPublicNetwork(new() { MaximumPageResources = 1 });
        var scope = network.CreateScope();
        var loopback = new Uri("http://127.0.0.1/");
        await Assert.ThrowsAsync<HttpRequestException>(() => scope.LoadAsync(loopback, CancellationToken.None, HtmlPublicRequestKind.Navigation));
        await Assert.ThrowsAsync<HttpRequestException>(() => scope.LoadAsync(loopback, CancellationToken.None, HtmlPublicRequestKind.Style));
        Assert.False(scope.IsExhausted);
        await Assert.ThrowsAsync<HtmlPublicNetworkLimitException>(() => scope.LoadAsync(loopback, CancellationToken.None, HtmlPublicRequestKind.Fetch));
        Assert.True(scope.IsExhausted);
        Assert.False(network.CreateScope().IsExhausted);
    }

    [Fact]
    public void RedirectAndConnectionBudgetsAreFiniteAndSticky()
    {
        var budget = new HtmlPublicNetwork.Budget(1, 1, 1_000);
        budget.TakeRedirect();
        Assert.Throws<HtmlPublicNetworkLimitException>(budget.TakeRedirect);
        Assert.Throws<HtmlPublicNetworkLimitException>(budget.TakeConnection);

        budget = new HtmlPublicNetwork.Budget(1, 1, 1_000);
        for (int i = 0; i < 3; i++) budget.TakeConnection();
        Assert.Throws<HtmlPublicNetworkLimitException>(budget.TakeConnection);
        Assert.True(budget.IsExhausted);
    }

    [Fact]
    public async Task BodyBytesAreChargedAcrossResponsesAndUnknownLengthBodiesAreBounded()
    {
        var budget = new HtmlPublicNetwork.Budget(2, 2, 7);
        using var first = new ByteArrayContent([1, 2, 3, 4]);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await HtmlPublicNetwork.ReadBodyAsync(first, HttpMethod.Get, 5, budget, CancellationToken.None));
        using var second = new StreamContent(new NonSeekableStream([5, 6, 7, 8]));
        Assert.Null(second.Headers.ContentLength);
        await Assert.ThrowsAsync<HtmlPublicNetworkLimitException>(() => HtmlPublicNetwork.ReadBodyAsync(
            second, HttpMethod.Get, 5, budget, CancellationToken.None));
        Assert.True(budget.IsExhausted);
    }

    [Fact]
    public async Task OversizedDeclaredBodiesAreRejectedBeforeReadingAndHeadDoesNotSpendBodyBytes()
    {
        var budget = new HtmlPublicNetwork.Budget(2, 2, 1000);
        using var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentLength = 100;
        Assert.Empty(await HtmlPublicNetwork.ReadBodyAsync(content, HttpMethod.Head, 5, budget, CancellationToken.None));
        Assert.False(budget.IsExhausted);
        await Assert.ThrowsAsync<HtmlPublicNetworkLimitException>(() => HtmlPublicNetwork.ReadBodyAsync(
            content, HttpMethod.Get, 5, budget, CancellationToken.None));
        Assert.True(budget.IsExhausted);
    }

    [Fact]
    public async Task SocketStreamAccountsForBothDirectionsIncludingReadAhead()
    {
        var budget = new HtmlPublicNetwork.Budget(0, 0, 0);
        using var inner = new MemoryStream();
        using var stream = new HtmlPublicNetwork.AccountedNetworkStream(inner, budget);
        byte[] payload = new byte[32 * 1024 + 1];
        await stream.WriteAsync(payload);
        inner.Position = 0;
        Assert.Equal(payload.Length - 3, stream.Read(payload.AsSpan(0, payload.Length - 3)));
        await Assert.ThrowsAsync<HtmlPublicNetworkLimitException>(() => stream.ReadAsync(payload.AsMemory()).AsTask());
        Assert.True(budget.IsExhausted);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
