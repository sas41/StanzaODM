using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Agents;
using Stanza.ControlPlane.Audit;
using Stanza.ControlPlane.Automation;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Sources;
using Stanza.ControlPlane.Transport;
using Stanza.Core.Contracts;
using Stanza.Tests.Automation;

namespace Stanza.Tests.ImageProbe;

public sealed class ControlPlaneImageProbeTests
{
    [Fact]
    public async Task UsesAppliedFilesOrderedEnvironmentPinnedCommitsFreshCredentialsAndCurrentRuntimeAgent()
    {
        await using var f = await Fixture.Create();
        var result = await f.Probe.ProbeAsync(f.Store.Root, f.Deployment, ["app"]);
        var request = Assert.Single(f.Dispatcher.Requests);
        Assert.NotEqual(f.Applied.Id, request.Id); Assert.Equal("image-digests", request.Operation); Assert.Null(request.Execution);
        Assert.Equal(f.Agent, f.Dispatcher.Agent); Assert.Equal(f.Applied.Files[0].Content, request.Files[0].Content);
        Assert.Equal(new[] { "global.env", "agent.env", "deployment.env" }, request.EnvironmentFiles.Select(x => x.Name));
        Assert.Equal(f.Applied.EnvironmentFiles.Select(x => x.Text), request.EnvironmentFiles.Select(x => x.Text));
        Assert.Equal(new string('a', 40), Assert.Single(request.Sources!).Reference);
        Assert.Equal("fresh-private-password", request.Sources![0].Password); Assert.Equal("fresh-registry-password", Assert.Single(request.Registries!).Password);
        Assert.Equal(2, f.Sources.Calls); Assert.Equal(ImageProbeRunner.Sha('a'), Assert.Single(result).CurrentDigest);
        var audit = await f.Store.Db.Set<AuditEvent>().AsNoTracking().Where(x => x.Action.StartsWith("image-probe.")).ToListAsync();
        Assert.Equal(2, audit.Count); var json = JsonSerializer.Serialize(audit);
        Assert.DoesNotContain("password", json); Assert.DoesNotContain("ENV_SECRET", json);
    }

    [Fact]
    public async Task OrdinaryActorNeedsUseNotRevealAndCannotReceiveAppliedSecrets()
    {
        await using var f = await Fixture.Create(); var s = f.Store;
        await s.Grant("automation.read", "deployment", f.Deployment); await s.Grant("agent.read", "agent", f.Agent);
        await s.Grant("deployment.read", "deployment", f.Deployment);
        await s.Grant("credential.use", "credential", f.Sources.Credential);
        var result = await f.Probe.ProbeAsync(s.User, f.Deployment, ["app"]);
        Assert.Single(result); var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("password", json); Assert.DoesNotContain("ENV_SECRET", json);
        Assert.False(await s.Access.IsAllowedAsync(s.User, "credential.reveal", "credential", f.Sources.Credential));
    }

    [Theory]
    [InlineData("automation")][InlineData("agent")][InlineData("credential")][InlineData("disabled")][InlineData("approval")]
    public async Task CurrentPermissionsIdentityAndApprovalAreRequiredWithoutRevealGrants(string denied)
    {
        await using var f = await Fixture.Create(); var s = f.Store;
        await s.Grant("deployment.read", "deployment", f.Deployment);
        if (denied != "automation") await s.Grant("automation.read", "deployment", f.Deployment);
        if (denied != "agent") await s.Grant("agent.read", "agent", f.Agent);
        if (denied != "credential") await s.Grant("credential.use", "credential", f.Sources.Credential);
        if (denied == "disabled") await s.Accounts.SetEnabledAsync(s.Root, s.User, false);
        if (denied == "approval") await s.Db.Set<AgentEntity>().Where(x => x.Id == f.Agent).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, "revoked"));
        var error = await Record.ExceptionAsync(() => f.Probe.ProbeAsync(s.User, f.Deployment, ["app"]));
        Assert.True(error is AutomationAccessException or SourceAccessException, error?.ToString()); Assert.Empty(f.Dispatcher.Requests);
    }

    [Theory]
    [InlineData("draft")][InlineData("environment")][InlineData("sources")][InlineData("operation")][InlineData("applied")][InlineData("commit")]
    public async Task PendingOrUnknownAppliedConfigurationCannotBecomeAnAutomaticRedeploy(string problem)
    {
        await using var f = await Fixture.Create(); var db = f.Store.Db;
        switch (problem)
        {
            case "draft": await db.Set<DeploymentDraftEntity>().Where(x => x.Id == f.Deployment).ExecuteUpdateAsync(u => u.SetProperty(x => x.Revision, 2)); break;
            case "environment": await db.Set<AgentEntity>().Where(x => x.Id == f.Agent).ExecuteUpdateAsync(u => u.SetProperty(x => x.ProtectedEnvironment,
                f.Store.Protection.CreateProtector("Stanza.AgentEnvironment.v1").CreateProtector(f.Agent.ToString("N")).Protect("TAG=changed\n"))); break;
            case "sources": f.Sources.Reference = "changed-branch"; break;
            case "operation": db.Add(new OperationEntity { Id = Guid.NewGuid(), DeploymentId = f.Deployment, AgentId = f.Agent, State = "Running", Kind = "deploy" }); await db.SaveChangesAsync(); break;
            case "applied": await db.Set<OperationEntity>().ExecuteDeleteAsync(); break;
            case "commit": await db.Set<OperationEntity>().ExecuteUpdateAsync(u => u.SetProperty(x => x.ProtectedResponse, (string?)null)); break;
        }
        await Assert.ThrowsAsync<AutomationException>(() => f.Probe.ProbeAsync(f.Store.Root, f.Deployment, ["app"])); Assert.Empty(f.Dispatcher.Requests);
    }

    [Fact]
    public async Task RevocationOrDeploymentChangeDuringDispatchDiscardsResults()
    {
        await using var f = await Fixture.Create(); var s = f.Store;
        var read = await s.Grant("automation.read", "deployment", f.Deployment); await s.Grant("agent.read", "agent", f.Agent);
        await s.Grant("deployment.read", "deployment", f.Deployment);
        await s.Grant("credential.use", "credential", f.Sources.Credential);
        f.Dispatcher.BeforeReturn = () => s.Access.RemoveGrantAsync(s.Root, read.Id);
        await Assert.ThrowsAsync<AutomationAccessException>(() => f.Probe.ProbeAsync(s.User, f.Deployment, ["app"])); Assert.Single(f.Dispatcher.Requests);
    }

    [Theory]
    [InlineData("{}")][InlineData("[]")][InlineData("not json")]
    public async Task MalformedOrIncompleteAgentResultsNeverProduceEquality(string output)
    {
        await using var f = await Fixture.Create(); f.Dispatcher.Output = output;
        await Assert.ThrowsAsync<AutomationException>(() => f.Probe.ProbeAsync(f.Store.Root, f.Deployment, ["app"]));
    }

    [Fact]
    public async Task MissingServiceAndOfflineAgentFailClosed()
    {
        await using var f = await Fixture.Create();
        await Assert.ThrowsAsync<AutomationException>(() => f.Probe.ProbeAsync(f.Store.Root, f.Deployment, ["not-configured"]));
        f.Dispatcher.Connected = false;
        await Assert.ThrowsAsync<AgentUnavailableException>(() => f.Probe.ProbeAsync(f.Store.Root, f.Deployment, ["app"]));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public AutomationTestStore Store { get; private set; } = null!;
        public Guid Agent { get; } = Guid.NewGuid(); public Guid Deployment { get; private set; }
        public AgentWorkRequest Applied { get; private set; } = null!;
        public Sources Sources { get; private set; } = null!;
        public Dispatcher Dispatcher { get; } = new();
        public AgentImageDigestProbe Probe { get; private set; } = null!;
        public static async Task<Fixture> Create()
        {
            var f = new Fixture { Store = await AutomationTestStore.Create() }; var s = f.Store;
            s.Db.Add(new AgentEntity { Id = f.Agent, Identity = Guid.NewGuid().ToString("N"), Hostname = "probe-agent", Status = "approved",
                ProtectedEnvironment = s.Protection.CreateProtector("Stanza.AgentEnvironment.v1").CreateProtector(f.Agent.ToString("N")).Protect("AGENT_ENV_SECRET=applied\n") });
            await s.Db.SaveChangesAsync(); s.Db.ChangeTracker.Clear();
            var drafts = new DeploymentDraftService(s.Db, s.Protection, s.Accounts, s.Access, s.Clock);
            var draft = await drafts.CreateAsync(s.Root, "Applied", f.Agent, "services:\n  app:\n    image: registry.example/app:latest\n", "DEPLOY_ENV_SECRET=applied\n");
            f.Deployment = draft.Id;
            f.Applied = new(Guid.NewGuid(), f.Deployment, "deploy", [new("compose.yaml", System.Text.Encoding.UTF8.GetBytes("services:\n  app:\n    image: registry.example/app:latest\n"))],
                [new("global.env", ""), new("agent.env", "AGENT_ENV_SECRET=applied\n"), new("deployment.env", "DEPLOY_ENV_SECRET=applied\n")],
                Sources: [new("https://git.example/app.git", "main", "source", "user", "old-private-password")], Registries: [new("registry.example", "user", "old-registry-password")]);
            var protector = s.Protection.CreateProtector("Stanza.Operation.v1", f.Applied.Id.ToString("N"));
            var response = new AgentWorkResponse(f.Applied.Id, true, "succeeded", ResolvedSources: new Dictionary<string, string> { ["source"] = new('a', 40) });
            s.Db.Add(new OperationEntity { Id = f.Applied.Id, ActorId = s.Root, DeploymentId = f.Deployment, AgentId = f.Agent, Revision = 1,
                Kind = "deploy", State = "Succeeded", CompletedAt = 1, ProtectedRequest = protector.Protect(JsonSerializer.Serialize(f.Applied)), ProtectedResponse = protector.Protect(JsonSerializer.Serialize(response)) });
            s.Db.Add(new DeploymentRuntimeEntity { DeploymentId = f.Deployment, AgentId = f.Agent, Revision = 1, DesiredState = "running", ObservedState = "running" });
            await s.Db.SaveChangesAsync(); s.Db.ChangeTracker.Clear();
            f.Sources = new Sources(s); var operational = new OperationalPolicyService(s.Db, s.Access);
            f.Probe = new(s.Db, drafts, s.Access, f.Sources, new AgentPolicyService(s.Db, s.Access, operational), s.Protection,
                f.Dispatcher, new AuditService(s.Db, s.Clock, s.Access), new InstanceSettingsService(s.Db, s.Protection));
            return f;
        }
        public ValueTask DisposeAsync() => Store.DisposeAsync();
    }
    private sealed class Sources(AutomationTestStore store) : ISourceSnapshotProvider
    {
        public Guid Credential { get; } = Guid.NewGuid(); public int Calls { get; private set; }
        public string Reference { get; set; } = "main";
        public async Task<DeploymentSourcesSnapshot> PrepareAsync(Guid actorId, Guid deploymentId, CancellationToken token = default)
        {
            Calls++;
            if (!await store.Access.IsAllowedAsync(actorId, "credential.use", "credential", Credential, token)) throw new SourceAccessException();
            return new([new("https://git.example/app.git", Reference, "source", "user", "fresh-private-password")], [new("registry.example", "user", "fresh-registry-password")]);
        }
    }
    private sealed class Dispatcher : IAgentCommandDispatcher
    {
        public bool Connected { get; set; } = true; public Guid Agent { get; private set; }
        public List<AgentWorkRequest> Requests { get; } = [];
        public Func<Task>? BeforeReturn { get; set; }
        public string Output { get; set; } = JsonSerializer.Serialize(new[] { new AgentImageObservation("app", "registry.example/app:latest", "linux/arm64/v8", ImageProbeRunner.Sha('a'), ImageProbeRunner.Sha('b')) }, JsonSerializerOptions.Web);
        public bool IsConnected(Guid agentId) => Connected;
        public async Task<AgentWorkResponse> DispatchAsync(Guid agentId, AgentWorkRequest request, CancellationToken token = default)
        { Agent = agentId; Requests.Add(request); if (BeforeReturn is not null) await BeforeReturn(); return new(request.Id, true, "succeeded", Output); }
    }
}
