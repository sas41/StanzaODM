using Stanza.ControlPlane.Sources;

namespace Stanza.Tests.Sources;

public sealed class SourceValidationTests
{
    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ext::sh -c evil")]
    [InlineData("-https://evil.test/repo")]
    [InlineData("http://git.test/repo")]
    [InlineData("ssh://git@git.test/repo")]
    [InlineData("https://user:pass@git.test/repo")]
    [InlineData("https://git.test/repo?token=secret")]
    [InlineData("https://git.test/repo#fragment")]
    [InlineData("https://localhost/repo")]
    [InlineData("https://localhost./repo")]
    [InlineData("https://localhost.localdomain/repo")]
    [InlineData("https://x.localhost/repo")]
    [InlineData("https://127.0.0.1/repo")]
    [InlineData("https://[::1]/repo")]
    [InlineData("https://[ff02::1]/repo")]
    [InlineData("https://[::ffff:127.0.0.1]/repo")]
    [InlineData("https://169.254.169.254/repo")]
    [InlineData("https://metadata.google.internal/repo")]
    [InlineData("https://git.test/\nrepo")]
    public void Unsafe_endpoint_is_rejected(string value) => Assert.Throws<SourceException>(() => SourceValidation.Url(value));

    [Theory]
    [InlineData("https://github.com/team/repo.git")]
    [InlineData("https://git.intranet/team/repo.git")]
    [InlineData("https://10.10.1.5/team/repo.git")]
    public void Public_and_intranet_https_git_are_allowed(string value) => Assert.Equal(value, SourceValidation.Url(value));

    [Theory]
    [InlineData("registry.test:5443", "registry.test:5443")]
    [InlineData("https://registry.test:5443/", "registry.test:5443")]
    public void Registry_is_normalized_to_authority(string input, string expected) => Assert.Equal(expected, SourceValidation.Url(input, true));

    [Fact]
    public void Registry_path_is_rejected() => Assert.Throws<SourceException>(() => SourceValidation.Url("https://registry.test/team/repo", true));

    [Theory]
    [InlineData("-exec")]
    [InlineData("main~1")]
    [InlineData("main^{commit}")]
    [InlineData("a..b")]
    [InlineData("a@{b}")]
    [InlineData("a b")]
    [InlineData("refs//heads/main")]
    [InlineData(".hidden/ref")]
    [InlineData("branch.lock")]
    [InlineData("a:b")]
    public void Ref_expressions_and_injection_are_rejected(string reference) => Assert.Throws<SourceException>(() => SourceValidation.Reference(reference));

    [Theory]
    [InlineData("main")]
    [InlineData("refs/heads/feature/my-feature")]
    [InlineData("refs/tags/v1.2.3")]
    [InlineData("6a47b9dd98a584134cb2d0b778b98fd1abff7c65")]
    public void Branches_tags_and_commits_are_supported(string reference) => Assert.Equal(reference, SourceValidation.Reference(reference));

    [Theory]
    [InlineData("../src")]
    [InlineData("/etc")]
    [InlineData("a\\b")]
    [InlineData(".git")]
    [InlineData("foo/.git/bar")]
    [InlineData(".stanza-private")]
    [InlineData("con/file")]
    public void Unsafe_destination_is_rejected(string destination) => Assert.Throws<SourceException>(() => SourceValidation.Destination(destination));
}
