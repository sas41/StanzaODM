using Stanza.ControlPlane.Deployments;
using Stanza.ControlPlane.Editing;

namespace Stanza.Tests.Editing;

public sealed class RevisionDiffTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("one\r\ntwo\nthree\r", "one\r\nchanged\ntwo\nthree\r")]
    [InlineData("same\nsame\nsame", "same\nother\nsame")]
    [InlineData("a\nb\nc", "c\nb\na")]
    [InlineData("<script>&\r\n", "\uFEFF<script>\n")]
    [InlineData("x\n", "x")]
    [InlineData("a\nb", "")]
    [InlineData("", "a\nb")]
    public void Diff_reconstructs_both_inputs_exactly_including_line_endings(string before, string after)
    {
        var diff = RevisionDiff.Compare(before, after);
        Assert.Equal(before, string.Concat(diff.Where(x => x.Kind != "added").Select(x => x.Text)));
        Assert.Equal(after, string.Concat(diff.Where(x => x.Kind != "removed").Select(x => x.Text)));
    }

    [Fact]
    public void Large_unrelated_line_sets_use_bounded_memory_and_complete()
    {
        var before = string.Concat(Enumerable.Range(0, 40000).Select(x => $"before{x}\n"));
        var after = string.Concat(Enumerable.Range(0, 40000).Select(x => $"after{x}\n"));
        var diff = RevisionDiff.Compare(before, after);
        Assert.Equal(80000, diff.Count);
        Assert.Equal(40000, diff.Count(x => x.Kind == "removed"));
    }

    [Fact]
    public void Larger_than_one_MiB_diffs_are_refused() => Assert.Throws<DeploymentDraftException>(() =>
        RevisionDiff.Compare("", new string('x', DeploymentDraftService.MaximumTextBytes + 1)));
}
