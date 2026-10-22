using Stanza.Core.Deployments;

namespace Stanza.Tests.Deployments;

public sealed class DeploymentPathTests
{
    [Theory]
    [InlineData("compose.yaml")]
    [InlineData("build/Dockerfile")]
    [InlineData(".env")]
    [InlineData("data/file name.txt")]
    [InlineData("日本語/設定.txt")]
    [InlineData("a/.hidden/b")]
    public void Valid_paths_are_not_rewritten(string path) => Assert.Equal(path, DeploymentPath.Parse(path).Value);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("/etc/passwd")]
    [InlineData("//host/share")]
    [InlineData("C:/Windows/file")]
    [InlineData("C:relative")]
    [InlineData("\\\\server\\share")]
    [InlineData("a\\..\\secret")]
    [InlineData("../secret")]
    [InlineData("a/../secret")]
    [InlineData("./compose.yaml")]
    [InlineData("a//b")]
    [InlineData("a/")]
    [InlineData("a/.")]
    [InlineData("a/b.")]
    [InlineData("a/b ")]
    [InlineData("a/file:stream")]
    [InlineData("a/*")]
    [InlineData("a/?")]
    [InlineData("a/\"x")]
    [InlineData("a/<x")]
    [InlineData("a/x>")]
    [InlineData("a/x|y")]
    [InlineData("a/\0b")]
    [InlineData("a/\nb")]
    [InlineData("a/\tb")]
    [InlineData("NUL")]
    [InlineData("con.txt")]
    [InlineData("AUX")]
    [InlineData("prn")]
    [InlineData("com1.log")]
    [InlineData("lpt9")]
    [InlineData("COM¹.txt")]
    [InlineData("CON .txt")]
    [InlineData("CONIN$")]
    [InlineData("conout$.txt")]
    public void Unsafe_paths_are_rejected(string path) =>
        Assert.ThrowsAny<ArgumentException>(() => DeploymentPath.Parse(path));

    [Fact]
    public void Null_is_rejected() => Assert.Throws<ArgumentNullException>(() => DeploymentPath.Parse(null!));

    [Fact]
    public void Ancestry_is_segment_aware_and_case_insensitive()
    {
        var path = DeploymentPath.Parse("build");
        Assert.True(path.IsSameOrAncestorOf(DeploymentPath.Parse("BUILD")));
        Assert.True(path.IsSameOrAncestorOf(DeploymentPath.Parse("BUILD/file")));
        Assert.False(path.IsSameOrAncestorOf(DeploymentPath.Parse("builder/file")));
        Assert.False(DeploymentPath.Parse("build/file").IsSameOrAncestorOf(path));
        Assert.Throws<ArgumentNullException>(() => path.IsSameOrAncestorOf(null!));
    }
}
