using Stanza.Core.Deployments;

namespace Stanza.Tests.Deployments;

public sealed class DeploymentRevisionTests
{
    private static InMemoryDeploymentRevisionService Service() => new(new(100, 10000, 100000));

    [Fact]
    public void Snapshots_preserve_raw_text_and_isolate_saved_inputs_and_history()
    {
        var service = Service();
        var deployment = Guid.NewGuid();
        var agent = Guid.NewGuid();
        const string compose = "# exactly this text\r\nservices:\n  web: { image: nginx }\n";
        const string env = "# original\r\nA='x'\n";
        var files = new List<ManagedFileSnapshot> { ManagedFileSnapshot.Text("compose.yaml", compose), ManagedFileSnapshot.Binary("asset", [0, 255]) };
        var source = new GitSourceWorkspace(Guid.NewGuid(), "repo");
        var sources = new List<GitSourceWorkspace> { source };
        var first = service.Save(deployment, agent, 0, "compose.yaml", files, env, sources);
        files.Clear();
        sources.Clear();
        var secondAgent = Guid.NewGuid();
        var second = service.Save(deployment, secondAgent, 1, "new.yaml", [ManagedFileSnapshot.Text("new.yaml", "services: {}")], "X=changed");
        Assert.Equal(compose, first.ComposeText);
        Assert.Equal(env, first.EnvironmentText);
        Assert.Equal(agent, first.TargetAgentId);
        Assert.Equal(2, first.Files.Count);
        Assert.Same(source, Assert.Single(first.Sources));
        Assert.Same(first, service.Get(deployment, 1));
        Assert.Same(second, service.Get(deployment));
        Assert.Equal(2, second.Number);
        Assert.Equal(secondAgent, second.TargetAgentId);
        Assert.Throws<NotSupportedException>(() => ((IList<ManagedFileSnapshot>)first.Files).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<GitSourceWorkspace>)first.Sources).Clear());
        Assert.DoesNotContain(compose, first.ToString());
        Assert.DoesNotContain(env, first.ToString());
    }

    [Fact]
    public void Revision_conflicts_do_not_create_history_and_distinct_deployments_are_isolated()
    {
        var service = Service();
        var id = Guid.NewGuid();
        Save(service, id, 0);
        Assert.Throws<InvalidOperationException>(() => Save(service, id, 0));
        Assert.Throws<InvalidOperationException>(() => Save(service, id, 2));
        Assert.Equal(1, service.Get(id).Number);
        Assert.Equal(1, Save(service, Guid.NewGuid(), 0).Number);
        Assert.Equal(2, Save(service, id, 1).Number);
    }

    [Fact]
    public void Concurrent_saves_have_exactly_one_winner()
    {
        var service = Service();
        var id = Guid.NewGuid();
        Save(service, id, 0);
        var wins = 0;
        Parallel.For(0, 20, _ =>
        {
            try { Save(service, id, 1); Interlocked.Increment(ref wins); }
            catch (InvalidOperationException) { }
        });
        Assert.Equal(1, wins);
        Assert.Equal(2, service.Get(id).Number);
    }

    [Fact]
    public void File_operations_are_whole_snapshot_edits_not_mutations()
    {
        var service = Service();
        var id = Guid.NewGuid();
        var first = service.Save(id, Guid.NewGuid(), 0, "compose.yaml",
            [ManagedFileSnapshot.Text("compose.yaml", "services: {}"), ManagedFileSnapshot.Text("old.txt", "raw\r\n")], "");
        var second = service.Save(id, first.TargetAgentId, 1, "compose.yaml",
            [first.Files[0], first.Files[1].Rename("new.txt"), ManagedFileSnapshot.Directory("empty")], "");
        var third = service.Save(id, first.TargetAgentId, 2, "compose.yaml", [second.Files[0]], "");
        Assert.Equal("old.txt", first.Files[1].Path.Value);
        Assert.Equal("new.txt", second.Files[1].Path.Value);
        Assert.Equal("raw\r\n", second.Files[1].GetText());
        Assert.Single(third.Files);
    }

    [Fact]
    public void Compose_must_be_one_explicit_managed_text_path_and_domain_does_not_pretend_to_validate_yaml()
    {
        var service = Service();
        var id = Guid.NewGuid();
        var agent = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => service.Save(id, agent, 0, "compose.yaml", [], ""));
        Assert.Throws<ArgumentException>(() => service.Save(id, agent, 0, "compose.yaml", [ManagedFileSnapshot.Binary("compose.yaml", [])], ""));
        Assert.Throws<ArgumentException>(() => service.Save(id, agent, 0, "compose.yaml", [ManagedFileSnapshot.Directory("compose.yaml")], ""));
        Assert.Throws<ArgumentException>(() => service.Save(id, agent, 0, "compose.yaml", [ManagedFileSnapshot.Text("Compose.yaml", "")], ""));
        var saved = service.Save(id, agent, 0, "compose.yaml", [ManagedFileSnapshot.Text("compose.yaml", "invalid yaml : [")], "");
        Assert.Equal("invalid yaml : [", saved.ComposeText); // Runtime adapter must validate before execution.
    }

    [Fact]
    public void Invalid_arguments_and_missing_revisions_fail_without_creating_deployments()
    {
        var service = Service();
        var id = Guid.NewGuid();
        var agent = Guid.NewGuid();
        ManagedFileSnapshot[] files = [ManagedFileSnapshot.Text("compose.yaml", "")];
        Assert.Throws<ArgumentNullException>(() => new InMemoryDeploymentRevisionService(null!));
        Assert.Throws<ArgumentException>(() => service.Save(Guid.Empty, agent, 0, "compose.yaml", files, ""));
        Assert.Throws<ArgumentException>(() => service.Save(id, Guid.Empty, 0, "compose.yaml", files, ""));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Save(id, agent, -1, "compose.yaml", files, ""));
        Assert.Throws<ArgumentNullException>(() => service.Save(id, agent, 0, "compose.yaml", null!, ""));
        Assert.Throws<ArgumentNullException>(() => service.Save(id, agent, 0, "compose.yaml", files, null!));
        Assert.Throws<System.Text.EncoderFallbackException>(() => service.Save(id, agent, 0, "compose.yaml", files, "A=\uD800"));
        Assert.Throws<KeyNotFoundException>(() => service.Get(id));
        Save(service, id, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Get(id, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Get(id, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Get(id, 2));
    }

    private static DeploymentRevision Save(InMemoryDeploymentRevisionService service, Guid id, long expected) =>
        service.Save(id, Guid.NewGuid(), expected, "compose.yaml", [ManagedFileSnapshot.Text("compose.yaml", "services: {}")], "");
}
