using Microsoft.EntityFrameworkCore;
using Stanza.ControlPlane.Automation;

namespace Stanza.Tests.Automation;

public sealed class ImageUpdateTests
{
    private static string Digest(char c) => "sha256:" + new string(c, 64);
    private static ImageDigestObservation Image(bool changed = true, bool built = false, bool registry = false, string platform = "linux/arm64") =>
        new("app", "private.example/app:latest", platform, Digest('a'), Digest(changed ? 'b' : 'a'), built, registry);

    [Theory]
    [InlineData(false, false, false, 0)][InlineData(true, false, false, 1)]
    [InlineData(true, true, false, 0)][InlineData(true, true, true, 1)]
    public void DigestComparisonIsArchitectureAwareAndExcludesUnpublishedLocalBuilds(bool changed, bool built, bool registry, int count) =>
        Assert.Equal(count, ImageUpdates.Changed([Image(changed, built, registry)], ["app"]).Count);

    [Fact]
    public void UnknownBaselineDoesNotDeployAndIncompleteOrInvalidProbeFails()
    {
        Assert.Empty(ImageUpdates.Changed([Image() with { CurrentDigest = null }], ["app"]));
        Assert.Throws<AutomationException>(() => ImageUpdates.Changed([], ["app"]));
        Assert.Throws<AutomationException>(() => ImageUpdates.Changed([Image() with { RemoteDigest = "latest" }], ["app"]));
        Assert.Throws<AutomationException>(() => ImageUpdates.Changed([Image() with { Platform = "" }], ["app"]));
        Assert.Throws<AutomationException>(() => ImageUpdates.Changed([Image(), Image()], ["app"]));
        Assert.Throws<AutomationException>(() => ImageUpdates.Changed([Image()], ["other"]));
    }

    [Theory]
    [InlineData("notify-only", true, 0, "updates-available")][InlineData("auto-deploy", true, 1, "redeployed-health-verified")]
    [InlineData("auto-deploy", false, 0, "unchanged")]
    public async Task PoliciesReuseNormalDeploymentPipelineAndRecordIdentities(string mode, bool changed, int operations, string result)
    {
        await using var s = await AutomationTestStore.Create();
        var policy = await s.Service.SaveImagePolicyAsync(s.Root, new() { Name = "Images", DeploymentId = s.Deployment, Services = "app", Mode = mode, Enabled = true }, 0);
        var run = await s.Service.CheckImagesAsync(s.Root, policy.Id);
        var probe = new Probe([Image(changed)]); await s.Executor(probe).ExecuteNextAsync();
        Assert.Equal(operations, s.Operations.Submissions.Count);
        Assert.All(s.Operations.Submissions, x => { Assert.Equal("deploy", x.Kind); Assert.Equal("automation:" + run.Id.ToString("N"), x.Trigger); });
        var saved = await s.Db.Set<ImageUpdatePolicy>().AsNoTracking().SingleAsync(); Assert.Equal(result, saved.LastResult);
        Assert.NotNull(saved.LastCheckAt); Assert.Contains("linux/arm64", saved.DigestsJson); Assert.Equal(s.Root, probe.Actor);
        Assert.Equal("succeeded", (await s.Service.ReadRunAsync(s.Root, run.Id)).Run.State);
    }

    [Fact]
    public async Task MissingProbeAndRegistryFailureDoNotFabricateSuccessAndHonorBackoff()
    {
        await using var s = await AutomationTestStore.Create();
        var policy = await s.Service.SaveImagePolicyAsync(s.Root, new() { Name = "Images", DeploymentId = s.Deployment, Services = "app", Mode = "auto-deploy", FailureBackoffMinutes = 7 }, 0);
        var run = await s.Service.CheckImagesAsync(s.Root, policy.Id); await s.Executor().ExecuteNextAsync();
        Assert.Empty(s.Operations.Submissions); Assert.Equal("failed", (await s.Service.ReadRunAsync(s.Root, run.Id)).Run.State);
        var saved = await s.Db.Set<ImageUpdatePolicy>().AsNoTracking().SingleAsync(); Assert.Equal(s.Service.Now + 420, saved.NextRunAt);
        Assert.Equal("probe-failed-or-unavailable", saved.LastResult);
    }

    [Fact]
    public async Task FailedRedeployIsNotReportedAsAppliedAndPolicyChecksDoNotOverlap()
    {
        await using var s = await AutomationTestStore.Create(); s.Operations.State = "Failed";
        var policy = await s.Service.SaveImagePolicyAsync(s.Root, new() { Name = "Images", DeploymentId = s.Deployment, Services = "app", Mode = "auto-deploy" }, 0);
        var run = await s.Service.CheckImagesAsync(s.Root, policy.Id); await s.Executor(new Probe([Image()])).ExecuteNextAsync();
        Assert.Equal("failed", (await s.Service.ReadRunAsync(s.Root, run.Id)).Run.State);
        Assert.Equal("redeploy-failed", (await s.Db.Set<ImageUpdatePolicy>().AsNoTracking().SingleAsync()).LastResult);
        var first = await s.Service.CheckImagesAsync(s.Root, policy.Id); var second = await s.Service.CheckImagesAsync(s.Root, policy.Id);
        Assert.True(await s.Executor().ClaimAsync(first)); Assert.False(await s.Executor().ClaimAsync(second));
        Assert.Equal("skipped", (await s.Service.ReadRunAsync(s.Root, second.Id)).Run.State);
    }

    [Fact]
    public async Task RevokedPolicyOwnerCannotProbeOrDeploy()
    {
        await using var s = await AutomationTestStore.Create(); await s.Grant("automation.manage", "deployment", s.Deployment);
        var read = await s.Grant("deployment.read", "deployment", s.Deployment);
        var policy = await s.Service.SaveImagePolicyAsync(s.User, new() { Name = "Images", DeploymentId = s.Deployment, Services = "app" }, 0);
        var run = await s.Service.CheckImagesAsync(s.User, policy.Id); await s.Access.RemoveGrantAsync(s.Root, read.Id);
        var probe = new Probe([Image()]); await s.Executor(probe).ExecuteNextAsync();
        Assert.Null(probe.Actor); Assert.Empty(s.Operations.Submissions); Assert.Equal("denied", (await s.Service.ReadRunAsync(s.Root, run.Id)).Run.State);
    }

    [Fact]
    public async Task ManualChecksCannotBorrowOwnerDeployPrivilegeAndChangedPolicyCannotExecuteStaleMode()
    {
        await using var s = await AutomationTestStore.Create();
        var policy = await s.Service.SaveImagePolicyAsync(s.Root, new() { Name = "Root policy", DeploymentId = s.Deployment, Services = "app", Mode = "auto-deploy" }, 0);
        await s.Grant("automation.manage", "deployment", s.Deployment); await s.Grant("deployment.read", "deployment", s.Deployment);
        await Assert.ThrowsAsync<AutomationAccessException>(() => s.Service.CheckImagesAsync(s.User, policy.Id));
        var run = await s.Service.CheckImagesAsync(s.Root, policy.Id);
        await s.Service.SaveImagePolicyAsync(s.Root, new() { Id = policy.Id, Name = "Changed to notify-only", DeploymentId = s.Deployment, Services = "app", Mode = "notify-only" }, 1);
        var probe = new Probe([Image()]); await s.Executor(probe).ExecuteNextAsync();
        Assert.Null(probe.Actor); Assert.Empty(s.Operations.Submissions); Assert.Equal("denied", (await s.Service.ReadRunAsync(s.Root, run.Id)).Run.State);
    }

    [Theory]
    [InlineData(false, "baseline-unknown")][InlineData(true, "updates-available-baseline-unknown")]
    public async Task UnknownSelectedBaselineIsNotReportedUnchangedAndBlocksWholeDeploymentAutoApply(bool otherUpdate, string result)
    {
        await using var s = await AutomationTestStore.Create();
        var services = otherUpdate ? "app,worker" : "app";
        var policy = await s.Service.SaveImagePolicyAsync(s.Root, new() { Name = "Uninstalled baseline", DeploymentId = s.Deployment, Services = services, Mode = "auto-deploy" }, 0);
        await s.Service.CheckImagesAsync(s.Root, policy.Id);
        var images = new List<ImageDigestObservation> { Image() with { CurrentDigest = null } };
        if (otherUpdate) images.Add(Image() with { Service = "worker" });
        await s.Executor(new Probe(images)).ExecuteNextAsync();
        Assert.Empty(s.Operations.Submissions);
        Assert.Equal(result, (await s.Db.Set<ImageUpdatePolicy>().AsNoTracking().SingleAsync()).LastResult);
    }

    [Theory]
    [InlineData("image-probe-pending-manual-changes", "image-probe-pending-manual-changes")]
    [InlineData("private-password registry diagnostic must not be shown", "probe-failed-or-unavailable")]
    public async Task ProbeFailuresExposeOnlyKnownActionableCodesAndAlwaysUseFailureBackoff(string error, string expected)
    {
        await using var s = await AutomationTestStore.Create();
        var policy = await s.Service.SaveImagePolicyAsync(s.Root, new() { Name = "Probe diagnostics", DeploymentId = s.Deployment,
            Services = "app", FrequencyMinutes = 5, FailureBackoffMinutes = 17 }, 0);
        await s.Service.CheckImagesAsync(s.Root, policy.Id);
        await s.Executor(new FailureProbe(error)).ExecuteNextAsync();
        var saved = await s.Db.Set<ImageUpdatePolicy>().AsNoTracking().SingleAsync();
        Assert.Equal(expected, saved.LastResult); Assert.Equal(s.Service.Now + 17 * 60, saved.NextRunAt);
        Assert.DoesNotContain("private-password", saved.LastResult);
    }

    private sealed class FailureProbe(string error) : IImageDigestProbe
    {
        public Task<IReadOnlyList<ImageDigestObservation>> ProbeAsync(Guid actorId, Guid deploymentId, IReadOnlyList<string> services, CancellationToken ct = default) =>
            throw new AutomationException(error);
    }

    private sealed class Probe(IReadOnlyList<ImageDigestObservation> images) : IImageDigestProbe
    {
        public Guid? Actor { get; private set; }
        public Task<IReadOnlyList<ImageDigestObservation>> ProbeAsync(Guid actorId, Guid deploymentId, IReadOnlyList<string> services, CancellationToken ct = default)
        { Actor = actorId; return Task.FromResult(images); }
    }
}
