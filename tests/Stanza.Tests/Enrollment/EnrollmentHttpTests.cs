using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Stanza.Agent.Enrollment;
using Stanza.Core.Contracts;
using Stanza.ControlPlane.Agents;
using Stanza.Tests.Web;

namespace Stanza.Tests.Enrollment;

public sealed class EnrollmentHttpTests
{
    [Fact]
    public async Task ActualAgentClientEnrollsPendingThenObservesApprovalAndRevocationOverInProcessHttp()
    {
        await using var factory = new StanzaFactory();
        var ownerId = await factory.BootstrapAsync();
        string token;
        using (var scope = factory.Services.CreateScope())
            token = (await scope.ServiceProvider.GetRequiredService<AgentAdministrationService>().IssueTokenAsync(ownerId, 15)).Token;
        using var key = new EnrollmentKey();
        using var http = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var client = new EnrollmentClient(http);
        Assert.Equal(EnrollmentStatus.Pending, await client.RegisterAsync(http.BaseAddress!, key.Request(token, DateTimeOffset.UtcNow), default));
        Guid agentId;
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<AgentAdministrationService>();
            agentId = Assert.Single(await service.ListAsync(ownerId)).Id;
            await service.SetStatusAsync(ownerId, agentId, AgentEnrollmentProtocol.Approved);
        }
        var approved = await client.RegisterAsync(http.BaseAddress!, key.Request(token, DateTimeOffset.UtcNow), default);
        Assert.Equal(EnrollmentStatus.Approved, approved);
        Assert.False(new AgentSnapshot(true, approved, key.Identity).CanExecuteWork);
        Assert.False(new AgentSnapshot(true, approved, key.Identity).CommandChannelAvailable);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AgentAdministrationService>().SetStatusAsync(ownerId, agentId, AgentEnrollmentProtocol.Revoked);
        Assert.Equal(EnrollmentStatus.Revoked, await client.RegisterAsync(http.BaseAddress!, key.Request(token, DateTimeOffset.UtcNow), default));
    }

    [Fact]
    public async Task EnrollmentEndpointRejectsReplayBadSignaturesAndIncompatibleVersionsWithoutDisclosingToken()
    {
        await using var factory = new StanzaFactory();
        var ownerId = await factory.BootstrapAsync();
        string token;
        using (var scope = factory.Services.CreateScope())
            token = (await scope.ServiceProvider.GetRequiredService<AgentAdministrationService>().IssueTokenAsync(ownerId, 15)).Token;
        using var key = new EnrollmentKey();
        using var http = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var request = key.Request(token, DateTimeOffset.UtcNow);
        using var accepted = await http.PostAsJsonAsync(AgentEnrollmentProtocol.Endpoint, request);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(new AgentEnrollmentResponse(1, AgentEnrollmentProtocol.Pending), await accepted.Content.ReadFromJsonAsync<AgentEnrollmentResponse>());
        foreach (var invalid in new[]
        {
            (request, HttpStatusCode.Conflict),
            (request with { Signature = new string('!', 88) }, HttpStatusCode.Unauthorized),
            (request with { ProtocolVersion = 2 }, HttpStatusCode.UpgradeRequired)
        })
        {
            using var response = await http.PostAsJsonAsync(AgentEnrollmentProtocol.Endpoint, invalid.Item1);
            Assert.Equal(invalid.Item2, response.StatusCode);
            Assert.DoesNotContain(token, await response.Content.ReadAsStringAsync());
        }
        using var nullResponse = await http.PostAsJsonAsync<AgentEnrollmentRequest?>(AgentEnrollmentProtocol.Endpoint, null);
        Assert.Equal(HttpStatusCode.BadRequest, nullResponse.StatusCode);
    }
}
