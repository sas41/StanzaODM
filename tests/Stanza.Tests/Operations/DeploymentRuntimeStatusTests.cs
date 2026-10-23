using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Controllers;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Models;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.ControlPlane.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace Stanza.Tests.Operations;

public sealed class DeploymentRuntimeStatusTests
{
    [Fact]
    public async Task Desired_observed_revision_offline_and_freshness_are_separate_and_authorized()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        await h.InScope(async services =>
        {
            var db = services.GetRequiredService<StanzaDbContext>();
            var service = new DeploymentRuntimeStatusService(db, services.GetRequiredService<AccessService>(), h.Dispatcher,
                services.GetRequiredService<OperationalPolicyService>(), h.Clock);
            Assert.Null(await service.ReadAsync(h.Root, h.Deployment, default));
            db.Add(new DeploymentRuntimeEntity { DeploymentId = h.Deployment, AgentId = h.Agent, Revision = 1,
                DesiredState = "running", ObservedState = "absent", ObservedAt = h.Clock.GetUtcNow().ToUnixTimeSeconds() });
            await db.SaveChangesAsync();
            var state = (await service.ReadAsync(h.Root, h.Deployment, default))!;
            Assert.Equal("running", state.DesiredState); Assert.Equal("absent", state.ObservedState);
            Assert.Equal(1, state.Revision); Assert.True(state.AgentConnected); Assert.False(state.IsStale);
            Assert.Equal("Recent observation", state.Availability);
            h.Clock.Advance(TimeSpan.FromMinutes(1));
            state = (await service.ReadAsync(h.Root, h.Deployment, default))!;
            Assert.True(state.IsStale); Assert.Contains("Stale", state.Availability);
            h.Dispatcher.Connected = false;
            state = (await service.ReadAsync(h.Root, h.Deployment, default))!;
            Assert.False(state.AgentConnected); Assert.Contains("Offline", state.Availability);
            await Assert.ThrowsAsync<DeploymentDraftAccessException>(() => service.ReadAsync(h.User, h.Deployment, default));
            return true;
        });
    }

    [Fact]
    public async Task Deployment_controller_adds_runtime_metadata_to_list_and_detail_without_exposing_configuration()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        await h.GrantAsync("deployment.read", "deployment", h.Deployment);
        await h.InScope(async services =>
        {
            var db = services.GetRequiredService<StanzaDbContext>();
            db.Add(new DeploymentRuntimeEntity { DeploymentId = h.Deployment, AgentId = h.Agent, Revision = 1,
                DesiredState = "stopped", ObservedState = "stopped", ObservedAt = h.Clock.GetUtcNow().ToUnixTimeSeconds() });
            await db.SaveChangesAsync();
            var runtime = new DeploymentRuntimeStatusService(db, services.GetRequiredService<AccessService>(), h.Dispatcher,
                services.GetRequiredService<OperationalPolicyService>(), h.Clock);
            var controller = new DeploymentsController(services.GetRequiredService<DeploymentDraftService>(), runtimeStatus: runtime)
            { ControllerContext = new() { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, h.User.ToString())], "test")) } } };
            var list = Assert.IsType<DeploymentListModel>(Assert.IsType<ViewResult>(await controller.Index(default)).Model);
            Assert.Equal("stopped", Assert.Single(list.Runtime).Value.ObservedState);
            var detail = Assert.IsType<DeploymentDraftView>(Assert.IsType<ViewResult>(await controller.Details(h.Deployment, null, default)).Model);
            Assert.NotNull(detail.Runtime); Assert.Null(detail.ComposeText); Assert.Null(detail.EnvironmentText);
            return true;
        });
    }
}
