using System.Text;
using Stanza.Core.Deployments;

namespace Stanza.Tests.Deployments;

public sealed class ManagedFileSnapshotTests
{
    private static readonly ManagedFileLimits Limits = new(100, 1024, 4096);

    [Fact]
    public void Text_and_binary_round_trip_exactly_and_cannot_be_mutated()
    {
        const string text = "\uFEFF# untouched\r\n雪: 'x'\n\r\n";
        var file = ManagedFileSnapshot.Text("Dockerfile", text);
        Assert.Equal(text, file.GetText());
        Assert.Equal(Encoding.UTF8.GetBytes(text), file.GetBytes());
        Assert.Equal(Encoding.UTF8.GetByteCount(text), file.Length);
        var uploadedText = ManagedFileSnapshot.Utf8Text("upload.txt", file.GetBytes());
        Assert.Equal(file.GetBytes(), uploadedText.GetBytes());
        Assert.Equal(text, uploadedText.GetText());
        var returned = file.GetBytes();
        returned[0] = 0;
        Assert.Equal(text, file.GetText());

        byte[] original = [0, 255, 13, 10, 128];
        var binary = ManagedFileSnapshot.Binary("assets/image.bin", original);
        original[0] = 99;
        Assert.Equal(new byte[] { 0, 255, 13, 10, 128 }, binary.GetBytes());
        Assert.Throws<InvalidOperationException>(() => binary.GetText());
        var renamed = binary.Rename("renamed.bin");
        Assert.Equal(binary.GetBytes(), renamed.GetBytes());
        Assert.Equal("assets/image.bin", binary.Path.Value);
        Assert.Equal("renamed.bin", renamed.Path.Value);
        Assert.DoesNotContain(text, file.ToString());
    }

    [Fact]
    public void Directories_are_empty_and_not_text()
    {
        var directory = ManagedFileSnapshot.Directory("empty");
        Assert.Equal(ManagedEntryKind.Directory, directory.Kind);
        Assert.Equal(0, directory.Length);
        Assert.Empty(directory.GetBytes());
        Assert.Throws<InvalidOperationException>(() => directory.GetText());
        Assert.Throws<ArgumentException>(() => directory.Rename("../escape"));
    }

    [Fact]
    public void Invalid_utf16_is_not_silently_replaced_and_null_text_is_rejected()
    {
        Assert.Throws<EncoderFallbackException>(() => ManagedFileSnapshot.Text("bad.txt", "\uD800"));
        Assert.Throws<DecoderFallbackException>(() => ManagedFileSnapshot.Utf8Text("bad.txt", [0xFF, 0xFE]));
        Assert.Throws<ArgumentNullException>(() => ManagedFileSnapshot.Text("file", null!));
    }

    [Theory]
    [InlineData("a", "a")]
    [InlineData("a", "A")]
    [InlineData("a", "a/b")]
    [InlineData("a/b", "a")]
    [InlineData("Dir/a", "dir/b")]
    public void Duplicate_case_or_file_parent_collisions_are_rejected(string left, string right) =>
        Assert.Throws<ArgumentException>(() => Validate([ManagedFileSnapshot.Text(left, ""), ManagedFileSnapshot.Text(right, "")]));

    [Fact]
    public void Explicit_directories_can_contain_managed_files_and_git_destinations()
    {
        DeploymentWorkspaceValidator.Validate(
            [ManagedFileSnapshot.Directory("build"), ManagedFileSnapshot.Text("build/Dockerfile", "FROM scratch")],
            [new(Guid.NewGuid(), "build/repo")], Limits);
        Validate([ManagedFileSnapshot.Text("a", ""), ManagedFileSnapshot.Text("ab/file", "")]);
    }

    [Theory]
    [InlineData("repo", "repo")]
    [InlineData("repo/file", "repo")]
    [InlineData("repo", "repo/sub")]
    [InlineData("Repo/file", "repo")]
    [InlineData("Dir/file", "dir/repo")]
    public void Git_destinations_cannot_collide_with_managed_files(string managed, string destination) =>
        Assert.Throws<ArgumentException>(() => DeploymentWorkspaceValidator.Validate(
            [ManagedFileSnapshot.Text(managed, "")], [new(Guid.NewGuid(), destination)], Limits));

    [Fact]
    public void Git_destination_owns_even_an_empty_managed_directory()
    {
        Assert.Throws<ArgumentException>(() => DeploymentWorkspaceValidator.Validate(
            [ManagedFileSnapshot.Directory("repo")], [new(Guid.NewGuid(), "repo")], Limits));
        Assert.Throws<ArgumentException>(() => DeploymentWorkspaceValidator.Validate(
            [ManagedFileSnapshot.Directory("repo/empty")], [new(Guid.NewGuid(), "repo")], Limits));
    }

    [Theory]
    [InlineData("repo", "repo")]
    [InlineData("repo", "REPO")]
    [InlineData("repo", "repo/child")]
    [InlineData("repo/child", "repo")]
    [InlineData("Dir/a", "dir/b")]
    public void Git_destinations_cannot_overlap_or_differ_in_parent_case(string first, string second) =>
        Assert.Throws<ArgumentException>(() => DeploymentWorkspaceValidator.Validate(
            [], [new(Guid.NewGuid(), first), new(Guid.NewGuid(), second)], Limits));

    [Fact]
    public void Git_source_ids_are_unique_and_required_and_paths_are_validated()
    {
        var id = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => new GitSourceWorkspace(Guid.Empty, "repo"));
        Assert.Throws<ArgumentException>(() => new GitSourceWorkspace(id, "../repo"));
        Assert.Throws<ArgumentException>(() => DeploymentWorkspaceValidator.Validate([], [new(id, "one"), new(id, "two")], Limits));
        DeploymentWorkspaceValidator.Validate([], [new(id, "one"), new(Guid.NewGuid(), "two")], Limits);
    }

    [Fact]
    public void Limits_count_utf8_bytes_total_and_directory_entries_and_accept_exact_boundaries()
    {
        var limits = new ManagedFileLimits(2, 3, 4);
        DeploymentWorkspaceValidator.Validate([ManagedFileSnapshot.Text("a", "雪"), ManagedFileSnapshot.Text("b", "x")], [], limits);
        Assert.Throws<ArgumentException>(() => DeploymentWorkspaceValidator.Validate(
            [ManagedFileSnapshot.Text("a", "雪x")], [], limits));
        Assert.Throws<ArgumentException>(() => DeploymentWorkspaceValidator.Validate(
            [ManagedFileSnapshot.Text("a", "雪"), ManagedFileSnapshot.Text("b", "xx")], [], limits));
        Assert.Throws<ArgumentException>(() => DeploymentWorkspaceValidator.Validate(
            [ManagedFileSnapshot.Directory("a"), ManagedFileSnapshot.Directory("b"), ManagedFileSnapshot.Directory("c")], [], limits));
        DeploymentWorkspaceValidator.Validate([ManagedFileSnapshot.Text("empty", "")], [], new(1, 0, 0));
    }

    [Fact]
    public void Limit_configuration_and_null_inputs_are_validated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ManagedFileLimits(-1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ManagedFileLimits(-1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ManagedFileLimits(1, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ManagedFileLimits(1, 1, -1));
        Assert.Throws<ArgumentNullException>(() => DeploymentWorkspaceValidator.Validate(null!, [], Limits));
        Assert.Throws<ArgumentNullException>(() => DeploymentWorkspaceValidator.Validate([], null!, Limits));
        Assert.Throws<ArgumentNullException>(() => DeploymentWorkspaceValidator.Validate([], [], null!));
        Assert.Throws<ArgumentNullException>(() => Validate([null!]));
        Assert.Throws<ArgumentNullException>(() => DeploymentWorkspaceValidator.Validate([], [null!], Limits));
    }

    private static void Validate(ManagedFileSnapshot[] files) => DeploymentWorkspaceValidator.Validate(files, [], Limits);
}
