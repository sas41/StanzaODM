using Microsoft.EntityFrameworkCore;
using Stanza.Core.Contracts;
using Stanza.ControlPlane.Agents;

namespace Stanza.Tests.Enrollment;

public sealed class AgentEnvironmentTests
{
    [Fact]
    public async Task EnvironmentIsEncryptedAndRoundTripsExactTextWithoutChangingEnrollmentState()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        var agent = await RegisterAgentAsync(fixture);
        const string text = "\uFEFF# keep comments and line endings\r\nTOKEN='extremely-sensitive-secret'\r\nEMPTY=\n  # spacing\nMULTI=\"one\ntwo\"\nUNICODE=é🐈\n";
        var input = new AgentEnvironment(text, 0);
        await fixture.RunAsync(service => service.SaveEnvironmentAsync(fixture.OwnerId, agent.Id, input));
        var actual = await fixture.RunAsync(service => service.ReadEnvironmentAsync(fixture.OwnerId, agent.Id));
        Assert.Equal(text, actual.Text);
        Assert.Equal(1, actual.Version);
        Assert.DoesNotContain("extremely-sensitive-secret", input.ToString());
        await using var db = fixture.CreateContext();
        var stored = await db.Set<AgentEntity>().SingleAsync();
        Assert.DoesNotContain("extremely-sensitive-secret", stored.ProtectedEnvironment);
        Assert.DoesNotContain("keep comments", stored.ProtectedEnvironment);
        Assert.Equal(AgentEnrollmentProtocol.Pending, stored.Status);
        Assert.Equal(agent.LastSeenAt, stored.LastSeenAt);
    }

    [Fact]
    public async Task ReusedContextRejectsStaleEnvironmentVersionAndCanRecoverFromConflict()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        var agent = await RegisterAgentAsync(fixture);
        await using var staleContext = fixture.CreateContext();
        var staleService = fixture.Service(staleContext);
        await staleContext.Set<AgentEntity>().SingleAsync();
        var original = await staleService.ReadEnvironmentAsync(fixture.OwnerId, agent.Id);
        await fixture.RunAsync(service => service.SaveEnvironmentAsync(fixture.OwnerId, agent.Id, new("SECRET=first-writer\r\n", original.Version)));
        Assert.Equal(409, (await Assert.ThrowsAsync<AgentOperationException>(() => staleService.SaveEnvironmentAsync(fixture.OwnerId,
            agent.Id, new("SECRET=lost-update", original.Version)))).StatusCode);
        var latest = await staleService.ReadEnvironmentAsync(fixture.OwnerId, agent.Id);
        Assert.Equal("SECRET=first-writer\r\n", latest.Text);
        Assert.Equal(1, latest.Version);
        await staleService.SaveEnvironmentAsync(fixture.OwnerId, agent.Id, new("SECRET=explicit-new-revision", latest.Version));
        Assert.Equal(new AgentEnvironment("SECRET=explicit-new-revision", 2), await fixture.RunAsync(service => service.ReadEnvironmentAsync(fixture.OwnerId, agent.Id)));
    }

    [Fact]
    public async Task StatusChangeInvalidatesStaleEnvironmentEditor()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        var agent = await RegisterAgentAsync(fixture);
        await fixture.SetStatusAsync(agent.Id, AgentEnrollmentProtocol.Approved);
        Assert.Equal(409, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RunAsync(service =>
            service.SaveEnvironmentAsync(fixture.OwnerId, agent.Id, new("not-applied", 0))))).StatusCode);
        Assert.Equal(new AgentEnvironment("", 1), await fixture.RunAsync(service => service.ReadEnvironmentAsync(fixture.OwnerId, agent.Id)));
    }

    [Fact]
    public async Task ConcurrentEnvironmentEditsAtSameVersionYieldOneConflict()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        var agent = await RegisterAgentAsync(fixture);
        var outcomes = await EnrollmentReplayTests.RaceAsync(
            () => fixture.RunAsync(service => service.SaveEnvironmentAsync(fixture.OwnerId, agent.Id, new("VALUE=first", 0))),
            () => fixture.RunAsync(service => service.SaveEnvironmentAsync(fixture.OwnerId, agent.Id, new("VALUE=second", 0))));
        Assert.Single(outcomes, status => status == 200);
        Assert.Single(outcomes, status => status == 409);
        var actual = await fixture.RunAsync(service => service.ReadEnvironmentAsync(fixture.OwnerId, agent.Id));
        Assert.Equal(1, actual.Version);
        Assert.Contains(actual.Text, new[] { "VALUE=first", "VALUE=second" });
    }

    [Theory]
    [InlineData("null-environment")]
    [InlineData("null-text")]
    [InlineData("null-byte")]
    [InlineData("negative-version")]
    [InlineData("oversized-ascii")]
    [InlineData("oversized-utf8")]
    [InlineData("invalid-unicode")]
    public async Task MalformedEnvironmentIsRejectedWithoutChangingCiphertextOrVersion(string malformed)
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        var agent = await RegisterAgentAsync(fixture);
        AgentEnvironment? input = malformed switch
        {
            "null-environment" => null,
            "null-text" => new(null!, 0),
            "null-byte" => new("SECRET=sensitive\0", 0),
            "negative-version" => new("SECRET=sensitive", -1),
            "oversized-ascii" => new(new string('s', 1024 * 1024 + 1), 0),
            "oversized-utf8" => new(new string('é', 524289), 0),
            "invalid-unicode" => new("SECRET=\uD800", 0),
            _ => throw new ArgumentException("Unknown malformed case.")
        };
        await using var db = fixture.CreateContext();
        var originalCiphertext = (await db.Set<AgentEntity>().AsNoTracking().SingleAsync()).ProtectedEnvironment;
        var error = await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RunAsync(service => service.SaveEnvironmentAsync(fixture.OwnerId, agent.Id, input)));
        Assert.Equal(400, error.StatusCode);
        Assert.DoesNotContain("sensitive", error.ToString());
        Assert.Equal(originalCiphertext, (await db.Set<AgentEntity>().AsNoTracking().SingleAsync()).ProtectedEnvironment);
        Assert.Equal(new AgentEnvironment("", 0), await fixture.RunAsync(service => service.ReadEnvironmentAsync(fixture.OwnerId, agent.Id)));
    }

    [Fact]
    public async Task ExactUtf8ByteLimitIsAccepted()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        var agent = await RegisterAgentAsync(fixture);
        var text = new string('é', 524288);
        await fixture.RunAsync(service => service.SaveEnvironmentAsync(fixture.OwnerId, agent.Id, new(text, 0)));
        Assert.Equal(text, (await fixture.RunAsync(service => service.ReadEnvironmentAsync(fixture.OwnerId, agent.Id))).Text);
    }

    [Fact]
    public async Task EnvironmentCiphertextCannotBeTransferredToAnotherAgent()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        var first = await RegisterAgentAsync(fixture);
        var second = await RegisterAgentAsync(fixture);
        await fixture.RunAsync(service => service.SaveEnvironmentAsync(fixture.OwnerId, first.Id, new("SECRET=first-agent-only", 0)));
        await using var db = fixture.CreateContext();
        var cipher = (await db.Set<AgentEntity>().AsNoTracking().SingleAsync(entity => entity.Id == first.Id)).ProtectedEnvironment;
        await db.Set<AgentEntity>().Where(entity => entity.Id == second.Id).ExecuteUpdateAsync(update => update.SetProperty(entity => entity.ProtectedEnvironment, cipher));
        var error = await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RunAsync(service => service.ReadEnvironmentAsync(fixture.OwnerId, second.Id)));
        Assert.Equal(503, error.StatusCode);
        Assert.DoesNotContain("first-agent-only", error.ToString());
        Assert.DoesNotContain(cipher, error.ToString());
        Assert.Equal("SECRET=first-agent-only", (await fixture.RunAsync(service => service.ReadEnvironmentAsync(fixture.OwnerId, first.Id))).Text);
    }

    [Fact]
    public async Task MissingAgentReturnsControlledNotFoundForAuthorizedAdministrator()
    {
        await using var fixture = await EnrollmentTestStore.CreateAsync();
        var missing = Guid.NewGuid();
        Assert.Equal(404, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RunAsync(service => service.ReadEnvironmentAsync(fixture.OwnerId, missing)))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.RunAsync(service => service.SaveEnvironmentAsync(fixture.OwnerId, missing, new("", 0))))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<AgentOperationException>(() => fixture.SetStatusAsync(missing, AgentEnrollmentProtocol.Approved))).StatusCode);
    }

    internal static async Task<AgentSummary> RegisterAgentAsync(EnrollmentTestStore fixture)
    {
        using var key = new EnrollmentKey();
        var token = await fixture.IssueAsync();
        await fixture.RegisterAsync(key.Request(token.Token, fixture.Clock.GetUtcNow()));
        return Assert.Single(await fixture.ListAsync(), agent => agent.Identity == key.Identity);
    }
}
