using System.IO.Compression;
using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Editing;

namespace Stanza.Tests.Editing;

public sealed class ManagedArchiveTests
{
    [Theory]
    [InlineData("Dir/a", "dir/b")]
    [InlineData("a", "a")]
    [InlineData("a", "a/b")]
    [InlineData("Dir/", "dir/")]
    public void Colliding_paths_are_rejected(string a, string b) => Assert.Throws<DeploymentDraftException>(() =>
        ManagedArchive.ReadZip(Zip((a, [], 0), (b, [], 0)), "", DeploymentEditorLimits.Default));

    [Theory]
    [InlineData(0xa000)] // symbolic link
    [InlineData(0x1000)] // fifo
    [InlineData(0x6000)] // block device
    [InlineData(0xc000)] // socket
    public void Special_unix_entries_are_rejected(int mode) => Assert.Throws<DeploymentDraftException>(() =>
        ManagedArchive.ReadZip(Zip(("link", "target"u8.ToArray(), mode << 16)), "", DeploymentEditorLimits.Default));

    [Fact]
    public void Windows_reparse_points_are_rejected() => Assert.Throws<DeploymentDraftException>(() =>
        ManagedArchive.ReadZip(Zip(("link", [], 0x400)), "", DeploymentEditorLimits.Default));

    [Fact]
    public void Entry_file_total_and_compressed_input_limits_are_independently_enforced()
    {
        var zip = Zip(("a", new byte[1024], 0), ("b", new byte[1024], 0));
        Assert.Throws<DeploymentDraftException>(() => ManagedArchive.ReadZip(zip, "", new(1024, 4096, 4096, 1)));
        Assert.Throws<DeploymentDraftException>(() => ManagedArchive.ReadZip(zip, "", new(1024, 512, 4096, 10)));
        Assert.Throws<DeploymentDraftException>(() => ManagedArchive.ReadZip(zip, "", new(1024, 4096, 1500, 10)));
        Assert.Throws<DeploymentDraftException>(() => ManagedArchive.ReadZip(zip, "", new(1024, zip.Length - 1, 4096, 10)));
        Assert.Equal(2, ManagedArchive.ReadZip(zip, "", new(1024, 4096, 4096, 10)).Count);
    }

    [Fact]
    public void Highly_compressed_bomb_is_rejected_before_expansion()
    {
        var zip = Zip(("bomb", new byte[2 * DeploymentDraftService.MaximumUploadBytes], 0));
        Assert.True(zip.Length < DeploymentDraftService.MaximumUploadBytes);
        Assert.Throws<DeploymentDraftException>(() => ManagedArchive.ReadZip(zip, "", DeploymentEditorLimits.Default));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/root")]
    [InlineData("a\\b")]
    public void Destination_is_validated(string path) => Assert.Throws<DeploymentDraftException>(() =>
        ManagedArchive.ReadZip(Zip(("file", [], 0)), path, DeploymentEditorLimits.Default));

    [Fact]
    public void Invalid_zip_and_nonempty_directory_are_rejected()
    {
        Assert.Throws<DeploymentDraftException>(() => ManagedArchive.ReadZip([0, 255, 1], "", DeploymentEditorLimits.Default));
        Assert.Throws<DeploymentDraftException>(() => ManagedArchive.ReadZip(Zip(("dir/", [1], 0)), "", DeploymentEditorLimits.Default));
    }

    [Fact]
    public void Utf8_text_is_editable_without_changing_BOM_or_line_endings_and_other_assets_remain_binary()
    {
        var exact = System.Text.Encoding.UTF8.GetBytes("\uFEFFFROM scratch\r\n# mixed\n");
        var files = ManagedArchive.ReadZip(Zip(("Dockerfile", exact, 0), ("binary", [0, 255, 1], 0), ("invalid-utf8", [255], 0)), "", DeploymentEditorLimits.Default);
        Assert.Equal(Stanza.Core.Deployments.ManagedEntryKind.Text, files[0].Kind);
        Assert.Equal(exact, files[0].GetBytes());
        Assert.Equal(Stanza.Core.Deployments.ManagedEntryKind.Binary, files[1].Kind);
        Assert.Equal(Stanza.Core.Deployments.ManagedEntryKind.Binary, files[2].Kind);
    }

    private static byte[] Zip(params (string Path, byte[] Content, int Attributes)[] files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Path, CompressionLevel.SmallestSize);
                entry.ExternalAttributes = file.Attributes;
                using var stream = entry.Open(); stream.Write(file.Content);
            }
        return output.ToArray();
    }
}
