using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Configuration;

namespace Stanza.Tests.Configuration;

public sealed class TrustedProxyPolicyTests
{
    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("10.20.0.0/16")]
    [InlineData("2001:db8::/48")]
    public void ExplicitAddressesAndBoundedNetworksAreAccepted(string value) => Assert.Equal(string.IsNullOrEmpty(value) ? 0 : 1, TrustedProxyPolicy.Parse(value).Count);

    [Fact]
    public void ParsingTrimsEmptyEntriesAndDeduplicatesWithoutBroadeningTrust() =>
        Assert.Equal(new[] { "127.0.0.1", "10.20.0.0/16", "::1" }, TrustedProxyPolicy.Parse(" 127.0.0.1,\r\n10.20.0.0/16,127.0.0.1\n::1,,"));

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("proxy.example")]
    [InlineData("https://127.0.0.1")]
    [InlineData("127.0.0.1:8080")]
    [InlineData("10.0.0.0/33")]
    [InlineData("2001:db8::/129")]
    [InlineData("127.0.0.1;10.0.0.1")]
    public void InvalidOrTrustAllEntriesAreRejected(string value) => Assert.Throws<SettingsOperationException>(() => TrustedProxyPolicy.Parse(value));

    [Fact]
    public void ProxyListIsStrictlyBounded() => Assert.Throws<SettingsOperationException>(() => TrustedProxyPolicy.Parse(new string(' ', 4097)));

    [Theory]
    [InlineData("", "10.0.0.1", false)]
    [InlineData("", "127.0.0.1", false)]
    [InlineData("10.0.0.2", "10.0.0.1", false)]
    [InlineData("10.0.0.1", "10.0.0.1", true)]
    [InlineData("10.0.0.0/24", "10.0.0.1", true)]
    [InlineData("2001:db8::/48", "2001:db8::1", true)]
    public async Task OnlyConfiguredConnectingProxiesMayForwardSchemeAndRemoteAddress(string configured, string remote, bool trusted)
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync(); await using var db = fixture.Context();
        await fixture.Policies(db).SaveAsync(fixture.Owner, new(new() { TrustedProxyAddresses = configured }, 0));
        var context = await InvokeAsync(fixture.Policies(db), remote, "203.0.113.9", "https");
        Assert.Equal(trusted ? "https" : "http", context.Request.Scheme);
        Assert.Equal(trusted ? "203.0.113.9" : remote, context.Connection.RemoteIpAddress!.ToString());
        Assert.Equal("control.example", context.Request.Host.Value);
        Assert.Equal("https://browser.example", context.Request.Headers.Origin.ToString());
        Assert.Equal("evil-host.example", context.Request.Headers["X-Forwarded-Host"].ToString());
    }

    [Theory]
    [InlineData(1, "10.0.0.1,10.0.0.2", "10.0.0.2")]
    [InlineData(2, "10.0.0.1,10.0.0.2", "203.0.113.9")]
    [InlineData(2, "10.0.0.1", "10.0.0.2")]
    public async Task ForwardLimitAndTrustOfEveryIntermediateHopAreEnforced(int limit, string proxies, string expected)
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync(); await using var db = fixture.Context();
        await fixture.Policies(db).SaveAsync(fixture.Owner, new(new() { TrustedProxyAddresses = proxies, TrustedProxyForwardLimit = limit }, 0));
        var context = await InvokeAsync(fixture.Policies(db), "10.0.0.1", "203.0.113.9,10.0.0.2", "https,https");
        Assert.Equal(expected, context.Connection.RemoteIpAddress!.ToString());
        Assert.Equal("https://browser.example", context.Request.Headers.Origin.ToString());
    }

    [Fact]
    public async Task UiProxyChangesApplyToTheVeryNextRequestAndRevokingTrustIgnoresForgedHeaders()
    {
        await using var fixture = await ConfigurationTestStore.CreateAsync(); await using var db = fixture.Context(); var policies = fixture.Policies(db);
        await policies.SaveAsync(fixture.Owner, new(new() { TrustedProxyAddresses = "10.0.0.1" }, 0));
        Assert.Equal("https", (await InvokeAsync(policies, "10.0.0.1", "203.0.113.9", "https")).Request.Scheme);
        await policies.SaveAsync(fixture.Owner, new(new(), 1));
        Assert.Equal("http", (await InvokeAsync(policies, "10.0.0.1", "203.0.113.9", "https")).Request.Scheme);
    }

    private static async Task<DefaultHttpContext> InvokeAsync(OperationalPolicyService policies, string remote, string forwardedFor, string forwardedProto)
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(policies);
        await using var provider = services.BuildServiceProvider();
        var application = new ApplicationBuilder(provider); application.UseStanzaForwardedHeaders(); application.Run(_ => Task.CompletedTask);
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote); context.Request.Scheme = "http"; context.Request.Host = new("control.example");
        context.Request.Headers["X-Forwarded-For"] = forwardedFor; context.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
        context.Request.Headers["X-Forwarded-Host"] = "evil-host.example"; context.Request.Headers.Origin = "https://browser.example";
        await application.Build()(context); return context;
    }
}
