using System.Text.Json;
using Stanza.Agent.Runtime;
using Stanza.ControlPlane.Sources;
using Stanza.Core.Contracts;

namespace Stanza.Tests.Sources;

public sealed class GitReferenceListingTests
{
    [Fact]
    public void MetadataOrdersNewestFirstWithDeterministicNameTiesAndLegacyNamesRemainCompatible()
    {
        var response = new AgentWorkResponse(Guid.NewGuid(), true, "ok", JsonSerializer.Serialize(new[] { "main", "feature/recent", "old", "same" }),
            GitReferences: [new("main", new('a', 40), 100, "Alice"), new("feature/recent", new('b', 40), 200, "Bob"), new("same", new('c', 40), 100)]);
        var listing = GitReferenceListing.Read(response);
        Assert.Equal(["feature/recent", "main", "same", "old"], listing.Select(x => x.Name));
        Assert.Contains("bbbbbbbb", listing[0].Label); Assert.Contains("Bob", listing[0].Label); Assert.Contains("1970-01-01", listing[0].Label);
        Assert.Equal(["feature/recent", "main", "old", "same"], GitReferenceListing.Read(response with { GitReferences = null }).Select(x => x.Name));
    }

    [Fact]
    public void InvalidCommitDatesAndOversizedMetadataAreRejected()
    {
        var response = new AgentWorkResponse(Guid.NewGuid(), true, "ok", "[\"main\"]", GitReferences: [new("main", "not-a-commit")]);
        Assert.Throws<SourceException>(() => GitReferenceListing.Read(response));
        Assert.Throws<SourceException>(() => GitReferenceListing.Read(response with { GitReferences = [new("main", new('a', 40), long.MaxValue)] }));
        Assert.Throws<SourceException>(() => GitReferenceListing.Read(response with { GitReferences = [new("main", Author: new('x', 257))] }));
    }

    [Fact]
    public void AgentParsesSortsAndSanitizesDatesHashesAndAuthorsWithoutChangingReferenceValues()
    {
        var references = AgentWorkExecutor.ParseGitReferences($"refs/heads/main\t{new string('a', 40)}\t100\tAlice\nrefs/heads/feature/recent\t{new string('b', 40)}\t200\tBob\u001b\nrefs/tags/v1\t{new string('c', 40)}\t\t\n");
        Assert.Equal(["feature/recent", "main", "refs/tags/v1"], references.Select(x => x.Name));
        Assert.Equal("Bob", references[0].Author); Assert.Null(references[2].CommittedAt);
        Assert.Throws<ArgumentException>(() => AgentWorkExecutor.ParseGitReferences("bad\twrong\t0\tname"));
    }
}
