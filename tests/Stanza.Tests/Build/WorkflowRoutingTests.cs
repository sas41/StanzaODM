using System.Text.RegularExpressions;
using Stanza.Tests.Web;

namespace Stanza.Tests.Build;

public sealed class WorkflowRoutingTests
{
    private static string Workflow => File.ReadAllText(Path.Combine(ArchitectureTests.RepositoryRoot(), ".github/workflows/docker-images.yml"));

    [Fact]
    public void Tests_are_triggered_for_main_PRs_and_their_updates_but_not_feature_branch_pushes()
    {
        var triggers = Workflow.Split("permissions:", 2)[0];
        Assert.Contains("push:\n    branches: [\"main\"]", triggers);
        Assert.Contains("pull_request:\n    branches: [\"main\"]", triggers);
        Assert.Contains("types: [opened, synchronize, reopened, ready_for_review, edited]", triggers);
        Assert.Contains("workflow_dispatch:", triggers);
        Assert.Contains("tags: [\"[0-9][0-9][0-9][0-9]-*\"]", triggers);
        Assert.DoesNotContain("branches: [\"**\"]", triggers);
        Assert.DoesNotContain("paths:", triggers);
        Assert.DoesNotContain("paths-ignore:", triggers);
    }

    [Theory]
    [InlineData("verify", "github.event_name == 'pull_request' && github.base_ref == 'main'")]
    [InlineData("release", "github.event_name != 'pull_request'")]
    [InlineData("images", "github.event_name != 'pull_request'")]
    public void Every_job_has_an_explicit_PR_or_non_PR_guard(string job, string condition)
    {
        Assert.Contains("    if: " + condition + "\n", Job(job));
    }

    [Fact]
    public void Skipped_PR_test_job_cannot_skip_main_tag_or_manual_image_builds()
    {
        var images = Job("images");
        Assert.Contains("    needs: release\n", images);
        Assert.DoesNotContain("verify", images);
        Assert.DoesNotContain("needs:", Job("release"));
        Assert.Contains("BUILD_VERSION: ${{ needs.release.outputs.version }}", images);
        Assert.Contains("push: ${{ steps.image.outputs.publish == 'true' }}", images);
    }

    [Fact]
    public void Only_the_PR_job_runs_tests_and_the_required_check_keeps_a_stable_nonmatrix_identity()
    {
        var verify = Job("verify");
        Assert.Contains("    name: Test application\n", verify);
        Assert.DoesNotContain("matrix:", verify);
        Assert.DoesNotContain("needs:", verify);
        Assert.Contains("dotnet test Stanza.slnx --configuration Release", verify);
        Assert.Contains("node --test tests/Stanza.Tests/*/*.test.cjs", verify);
        Assert.DoesNotContain("continue-on-error", verify);
        Assert.DoesNotContain("|| true", verify);
        foreach (var job in new[] { "release", "images" })
        {
            var content = Job(job);
            Assert.DoesNotContain("dotnet test", content);
            Assert.DoesNotContain("node --test", content);
            Assert.DoesNotContain("Set up Chrome", content);
        }
        Assert.Single(Regex.Matches(Workflow, @"run: dotnet test "));
        Assert.Single(Regex.Matches(Workflow, @"run: node --test "));
        Assert.DoesNotContain("packages: write", verify);
        Assert.DoesNotContain("secrets.", verify);
    }

    private static string Job(string name)
    {
        var jobs = Workflow.Split("jobs:\n", 2)[1];
        var match = Regex.Match(jobs, @"(?ms)^  " + Regex.Escape(name) + @":\n(?<body>.*?)(?=^  [a-z][a-z0-9_-]*:\n|\z)");
        Assert.True(match.Success, "Expected workflow job " + name);
        return match.Groups["body"].Value;
    }
}
