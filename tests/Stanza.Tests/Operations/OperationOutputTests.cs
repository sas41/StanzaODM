using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Configuration;
using Stanza.ControlPlane.Operations;
using Stanza.ControlPlane.Persistence;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Operations;

public sealed class OperationOutputTests
{
    [Fact]
    public async Task ReplayedChunksAreIdempotentButConflictingReplayIsRejected()
    {
        await using var h = await OperationTestHarness.CreateAsync(); var operation = await h.EnqueueAsync();
        await h.InScope(async s =>
        {
            var store = s.GetRequiredService<OperationOutputStore>(); var chunk = new AgentOutputChunk(1, "stdout", "durably retained\n");
            await store.AppendAsync(operation.Id, chunk); await store.AppendAsync(operation.Id, chunk);
            Assert.Equal(1, await store.LastSequenceAsync(operation.Id)); Assert.Single(await store.ReadAsync(operation.Id, 0));
            await Assert.ThrowsAsync<IOException>(() => store.AppendAsync(operation.Id, chunk with { Text = "different output" }));
            Assert.Equal(chunk.Text, (await store.ReadAsync(operation.Id, 0)).Single().Text);
            return true;
        });
    }
    [Fact]
    public async Task UnresolvedOperationsRetainOutputAndDisconnectEvidenceBeyondNormalRetention()
    {
        await using var h = await OperationTestHarness.CreateAsync(); var operation = await h.EnqueueAsync();
        await h.SetStateAsync(operation.Id, "Unknown", h.Clock.GetUtcNow().AddDays(-100).ToUnixTimeSeconds());
        await h.InScope(async s =>
        {
            var db = s.GetRequiredService<StanzaDbContext>();
            await s.GetRequiredService<OperationalPolicyService>().SaveAsync(h.Root, new(new() { LogRetentionDays = 1, AuditRetentionDays = 1 }, 0));
            await s.GetRequiredService<OperationOutputStore>().AppendAsync(operation.Id, new(1, "stderr", "interruption evidence"));
            db.Add(new AgentDisconnectEntity { Id = Guid.NewGuid(), OperationId = operation.Id, At = h.Clock.GetUtcNow().AddDays(-100).ToUnixTimeSeconds() });
            await db.SaveChangesAsync(); return true;
        });
        await h.StartWorkerAsync(); await Task.Delay(100);
        await h.InScope(async s =>
        {
            Assert.Single(await s.GetRequiredService<OperationOutputStore>().ReadAsync(operation.Id, 0));
            Assert.Single(await s.GetRequiredService<StanzaDbContext>().Set<AgentDisconnectEntity>().ToListAsync()); return true;
        });
    }
    [Fact]
    public async Task WorkerPersistsEncryptedOutputWhileRunningAndKeepsItAfterFailure()
    {
        await using var h = await OperationTestHarness.CreateAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Dispatcher.Handler = async (_, request, token) =>
        {
            await h.Dispatcher.EmitAsync(request.Id, new(1, "stdout", "pulling image\n"));
            await h.Dispatcher.EmitAsync(request.Id, new(2, "stderr", "manifest not found\n"));
            recorded.TrySetResult();
            await release.Task.WaitAsync(token);
            return new(request.Id, false, "runtime_failed");
        };
        var operation = await h.EnqueueAsync();
        await h.StartWorkerAsync();
        await recorded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Running", (await h.RowAsync(operation.Id)).State);
        var chunks = await h.InScope(sp => sp.GetRequiredService<OperationOutputStore>().ReadAsync(operation.Id, 0));
        Assert.Equal(new long[] { 1, 2 }, chunks.Select(x => x.Sequence));
        Assert.Equal("manifest not found\n", chunks[1].Text);
        var encrypted = await h.InScope(sp => sp.GetRequiredService<StanzaDbContext>().Set<OperationOutputEntity>().AsNoTracking().ToListAsync());
        Assert.DoesNotContain("manifest", encrypted[1].ProtectedText);
        release.TrySetResult();
        await h.WaitStateAsync(operation.Id, "Failed");
        Assert.Equal(chunks, await h.InScope(sp => sp.GetRequiredService<OperationOutputStore>().ReadAsync(operation.Id, 0)));
        Assert.Equal(chunks.Skip(1), await h.InScope(sp => sp.GetRequiredService<OperationOutputStore>().ReadAsync(operation.Id, 1)));
    }

    [Fact]
    public async Task OutputPermissionIsSeparateFromOperationReadAndIsRecheckedAfterRevocation()
    {
        await using var h = await OperationTestHarness.CreateAsync(); await h.AuthorizeUserAsync();
        var operation = await h.EnqueueAsync();
        Assert.False((await h.DetailAsync(operation.Id, h.User)).OutputAllowed);
        var grant = await h.GrantAsync("logs.read", "deployment", h.Deployment);
        Assert.True(await h.InScope(sp => sp.GetRequiredService<OperationService>().CanReadOutputAsync(h.User, operation.Id)));
        await h.RevokeAsync(grant.Id);
        Assert.False(await h.InScope(sp => sp.GetRequiredService<OperationService>().CanReadOutputAsync(h.User, operation.Id)));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task ZeroRetentionKeepsChunksForeverWhileFiniteRetentionDeletesThem(int retention, bool retained)
    {
        await using var h = await OperationTestHarness.CreateAsync();
        var operation = await h.EnqueueAsync();
        await h.InScope(async sp =>
        {
            await sp.GetRequiredService<OperationalPolicyService>().SaveAsync(h.Root, new(new() { LogRetentionDays = retention }, 0));
            await sp.GetRequiredService<OperationOutputStore>().AppendAsync(operation.Id, new(1, "stderr", "failure history"));
            return true;
        });
        await h.SetStateAsync(operation.Id, "Failed", h.Clock.GetUtcNow().AddDays(-2).ToUnixTimeSeconds());
        await h.StartWorkerAsync();
        if (!retained) await OperationTestHarness.UntilAsync(async () =>
            !await h.InScope(sp => sp.GetRequiredService<StanzaDbContext>().Set<OperationOutputEntity>().AnyAsync()), "Output was not expired.");
        else await Task.Delay(150);
        Assert.Equal(retained, await h.InScope(sp => sp.GetRequiredService<StanzaDbContext>().Set<OperationOutputEntity>().AnyAsync()));
    }
}
