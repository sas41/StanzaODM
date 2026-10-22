using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Terminals;

namespace Stanza.Tests.Terminals;

public sealed class TerminalPageTests
{
    [Fact]
    public async Task PageUsesOnlySelfHostedAssetsAndExplicitTypedContainerExecutionFields()
    {
        await using var server = await TerminalTestServer.CreateAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync(server.Origin + "/terminals/" + server.Deployment)).StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", await server.CookieAsync());
        var response = await client.GetAsync(server.Origin + "/terminals/" + server.Deployment);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var html = await response.Content.ReadAsStringAsync();
        foreach (var path in new[] { "/vendor/xterm/xterm.js", "/vendor/xterm/xterm.css", "/vendor/xterm/addon-fit.js", "/js/terminal.js" })
        {
            Assert.Contains(path, html);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(server.Origin + path)).StatusCode);
        }
        Assert.Contains("name=\"targetType\"", html);
        Assert.Contains("value=\"/bin/sh\"", html);
        Assert.Contains("name=\"arguments\"", html);
        Assert.DoesNotContain("cdn.jsdelivr", html);
        Assert.DoesNotContain("unpkg", html);
        var versions = await client.GetStringAsync(server.Origin + "/vendor/xterm/versions.json");
        Assert.Contains("5.5.0", versions);
        Assert.Contains("0.10.0", versions);
        Assert.Contains("Permission is hereby granted, free of charge", await File.ReadAllTextAsync(Path.Combine(server.App.Environment.WebRootPath, "vendor/xterm/LICENSE")));
        Assert.Contains("Permission is hereby granted, free of charge", await File.ReadAllTextAsync(Path.Combine(server.App.Environment.WebRootPath, "vendor/xterm/LICENSE-addon-fit")));
        await server.MutateAsync(async db => (await db.Set<DeploymentRuntimeEntity>().FindAsync(server.Deployment))!.ObservedState = "stopped");
        var stopped = await client.GetAsync(server.Origin + "/terminals/" + server.Deployment);
        Assert.Equal(HttpStatusCode.Conflict, stopped.StatusCode);
        Assert.Contains("currently assigned Agent", await stopped.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("http://localhost", null, true)]
    [InlineData("http://localhost/", null, false)]
    [InlineData("http://localhost/path", null, false)]
    [InlineData("http://localhost?query=1", null, false)]
    [InlineData("http://localhost#fragment", null, false)]
    [InlineData("http://user@localhost", null, false)]
    [InlineData("https://public.example", "https://public.example", true)]
    [InlineData("http://localhost", "https://public.example", false)]
    [InlineData("https://forged.example", null, false)]
    public void OriginRequiresTheExactConfiguredOrDirectOriginAndNeverForwardedHeaders(string origin, string? configured, bool allowed)
    {
        var request = new DefaultHttpContext().Request;
        request.Scheme = "http";
        request.Host = new HostString("localhost");
        request.Headers.Origin = origin;
        request.Headers["X-Forwarded-Host"] = "forged.example";
        request.Headers["X-Forwarded-Proto"] = "https";
        Assert.Equal(allowed, TerminalExtensions.IsSameOrigin(request, configured));
    }

    [Fact]
    public void MissingAndMultipleOriginsAreRejected()
    {
        var request = new DefaultHttpContext().Request;
        request.Scheme = "http";
        request.Host = new HostString("localhost");
        Assert.False(TerminalExtensions.IsSameOrigin(request, null));
        request.Headers.Origin = new Microsoft.Extensions.Primitives.StringValues(["http://localhost", "https://evil.invalid"]);
        Assert.False(TerminalExtensions.IsSameOrigin(request, null));
    }
}
