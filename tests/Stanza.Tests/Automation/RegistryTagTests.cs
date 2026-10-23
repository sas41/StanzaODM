using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Stanza.Agent.Runtime;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Automation;

public sealed class RegistryTagTests
{
    [Theory]
    [InlineData("ghcr.io")][InlineData("my.azurecr.io")][InlineData("registry.private.test")]
    public async Task BearerAuthenticationAndPaginationUseOnlyBoundRepositoryCredentials(string host)
    {
        var calls = new List<(Uri Uri, string? Auth)>();
        using var http = new HttpClient(new Handler(request =>
        {
            calls.Add((request.RequestUri!, request.Headers.Authorization?.ToString()));
            if (request.RequestUri!.AbsolutePath == "/token")
            {
                Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
                Assert.Contains(Uri.EscapeDataString("repository:org/app:pull"), request.RequestUri.Query);
                return Json(new { token = "test-token" });
            }
            if (request.Headers.Authorization is null)
            {
                var denied = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                denied.Headers.WwwAuthenticate.Add(new("Bearer", $"realm=\"https://{host}/token\",service=\"{host}\",scope=\"repository:evil:push\""));
                return denied;
            }
            Assert.Equal("Bearer test-token", request.Headers.Authorization.ToString());
            if (request.RequestUri.Query.Contains("last=")) return Json(new { tags = new[] { "v2", "v3" } });
            var first = Json(new { tags = new[] { "v1", "v2" } });
            first.Headers.TryAddWithoutValidation("Link", $"</v2/org/app/tags/list?n=1000&last=v2>; rel=\"next\""); return first;
        }));
        var result = await new RegistryTagClient(http).ListAsync(host + "/org/app", [new(host, "qa-user", "private-password"), new("unrelated.test", "no", "never")], default);
        Assert.Equal(new[] { "v3", "v2", "v1" }, result); Assert.Equal(4, calls.Count);
        Assert.All(calls, c => Assert.Equal(host, c.Uri.Host));
        Assert.All(calls, c => Assert.DoesNotContain("private-password", c.Uri.ToString()));
    }

    [Fact]
    public async Task DockerHubUsesItsStandardTokenServiceAndLibraryNamespace()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.Host == "auth.docker.io") { Assert.Null(request.Headers.Authorization); return Json(new { access_token = "anonymous" }); }
            Assert.Equal("/v2/library/busybox/tags/list", request.RequestUri.AbsolutePath);
            if (request.Headers.Authorization is not null) return Json(new { tags = new[] { "latest" } });
            var denied = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            denied.Headers.WwwAuthenticate.Add(new("Bearer", "realm=\"https://auth.docker.io/token\",service=\"registry.docker.io\"")); return denied;
        }));
        Assert.Equal("latest", Assert.Single(await new RegistryTagClient(http).ListAsync("busybox", [], default)));
    }

    [Theory]
    [InlineData("https://evil.test/token")][InlineData("http://registry.test/token")][InlineData("https://user:secret@registry.test/token")]
    public async Task ChallengeCannotExfiltrateBoundCredentials(string realm)
    {
        var count = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            count++; var denied = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            denied.Headers.WwwAuthenticate.Add(new("Bearer", $"realm=\"{realm}\"")); return denied;
        }));
        await Assert.ThrowsAsync<IOException>(() => new RegistryTagClient(http).ListAsync("registry.test/app", [new("registry.test", "user", "secret")], default));
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData("https://evil.test/v2/app/tags/list?n=1")][InlineData("/v2/other/tags/list?n=1")]
    public async Task PaginationCannotEscapeRegistryOrRepository(string next)
    {
        using var http = new HttpClient(new Handler(_ => { var response = Json(new { tags = new[] { "v1" } }); response.Headers.TryAddWithoutValidation("Link", $"<{next}>; rel=\"next\""); return response; }));
        await Assert.ThrowsAsync<IOException>(() => new RegistryTagClient(http).ListAsync("registry.test/app", [], default));
    }

    [Fact]
    public async Task ListingDeniedIsNotReportedAsEmptyAndEmptyRepositoriesWork()
    {
        using var denied = new HttpClient(new Handler(_ => new(HttpStatusCode.Forbidden)));
        await Assert.ThrowsAsync<IOException>(() => new RegistryTagClient(denied).ListAsync("registry.test/app", [], default));
        using var empty = new HttpClient(new Handler(_ => Json(new { tags = (string[]?)null })));
        Assert.Empty(await new RegistryTagClient(empty).ListAsync("registry.test/app", [], default));
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
