using System.Text;
using Stanza.Core.Deployments;

namespace Stanza.Tests.Deployments;

public sealed class EnvironmentScopeTests
{
    [Fact]
    public void Runtime_plan_preserves_every_scope_and_orders_separate_files_without_reinterpreting_them()
    {
        const string global = "\uFEFF# raw\r\nBASE='line1\nline2'\nX=${BASE:-default}\n";
        const string agent = "X=\"escaped\\nvalue\"\r\nEMPTY=\r\n";
        const string deployment = "X=${X:?required}\nESC=$$literal\n";
        var snapshot = Snapshot(global, agent, deployment);
        Assert.Equal(global, snapshot.GlobalRawText);
        Assert.Equal(agent, snapshot.AgentRawText);
        Assert.Equal(deployment, snapshot.DeploymentRawText);
        Assert.Equal(new[] { EnvironmentScope.Global, EnvironmentScope.Agent, EnvironmentScope.Deployment }, snapshot.OrderedInputs.Select(input => input.Scope));
        Assert.Equal(new[] { "global.env", "agent.env", "deployment.env" }, snapshot.OrderedInputs.Select(input => input.FileName));
        Assert.Equal(Encoding.UTF8.GetBytes(global), snapshot.OrderedInputs[0].GetUtf8Bytes());
        Assert.Equal(Encoding.UTF8.GetBytes(agent), snapshot.OrderedInputs[1].GetUtf8Bytes());
        Assert.Equal(Encoding.UTF8.GetBytes(deployment), snapshot.OrderedInputs[2].GetUtf8Bytes());
        var bytes = snapshot.OrderedInputs[0].GetUtf8Bytes();
        bytes[0] = 0;
        Assert.Equal(Encoding.UTF8.GetBytes(global), snapshot.OrderedInputs[0].GetUtf8Bytes());
        Assert.Equal(ProcessEnvironmentInheritance.None, snapshot.ProcessEnvironment);
        Assert.Throws<NotSupportedException>(() => ((IList<EnvironmentFileInput>)snapshot.OrderedInputs).Clear());
        Assert.DoesNotContain(global, snapshot.ToString());
        Assert.DoesNotContain(global, snapshot.OrderedInputs[0].ToString());
    }

    [Fact]
    public void Capture_pins_deployment_revision_and_agent_without_rewriting_the_deployment_environment()
    {
        var service = new InMemoryDeploymentRevisionService(new(10, 1000, 1000));
        var id = Guid.NewGuid();
        var agent = Guid.NewGuid();
        var first = service.Save(id, agent, 0, "compose.yaml", [ManagedFileSnapshot.Text("compose.yaml", "services: {}")], "A=local\r\n");
        var old = EnvironmentScopeSnapshot.Capture(first, "A=global1", "A=agent1");
        var second = service.Save(id, Guid.NewGuid(), 1, "compose.yaml", first.Files, first.EnvironmentText);
        var moved = EnvironmentScopeSnapshot.Capture(second, "A=global2", "A=agent2");
        Assert.Equal(id, old.DeploymentId);
        Assert.Equal(agent, old.TargetAgentId);
        Assert.Equal(1, old.DeploymentRevision);
        Assert.Equal(second.TargetAgentId, moved.TargetAgentId);
        Assert.Equal(2, moved.DeploymentRevision);
        Assert.Equal("A=global1", old.GlobalRawText);
        Assert.Equal("A=agent1", old.AgentRawText);
        Assert.Equal(first.EnvironmentText, moved.DeploymentRawText);
        Assert.Equal(first.EnvironmentText, old.DeploymentRawText);
    }

    [Fact]
    public void Literal_preview_layers_scopes_tracks_lines_and_overrides_and_distinguishes_key_case()
    {
        var snapshot = Snapshot("# comment\r\nA=global\r\nB=global\r\nC=global\r\na=lower\r\n",
            "A=agent\nB=agent\n", "# deployment\nA=deployment\nEMPTY=\n_GREETING1=hello world\nURL=https://host:123/a?x=y\n");
        var result = EnvironmentLiteralPreviewScanner.Scan(snapshot);
        Assert.True(result.IsSupported);
        Assert.Empty(result.Diagnostics);
        Assert.Equal("deployment", result.Values["A"].Value);
        Assert.Equal(EnvironmentScope.Deployment, result.Values["A"].Source);
        Assert.Equal(new[] { new EnvironmentKeyOccurrence(EnvironmentScope.Global, 2), new EnvironmentKeyOccurrence(EnvironmentScope.Agent, 1), new EnvironmentKeyOccurrence(EnvironmentScope.Deployment, 2) }, result.Values["A"].Occurrences);
        Assert.Equal("agent", result.Values["B"].Value);
        Assert.Equal(EnvironmentScope.Agent, result.Values["B"].Source);
        Assert.Equal("global", result.Values["C"].Value);
        Assert.Equal(EnvironmentScope.Global, result.Values["C"].Source);
        Assert.Equal("lower", result.Values["a"].Value);
        Assert.Equal("", result.Values["EMPTY"].Value);
        Assert.Equal("hello world", result.Values["_GREETING1"].Value);
        Assert.Equal("https://host:123/a?x=y", result.Values["URL"].Value);
        Assert.DoesNotContain("hello world", result.Values["_GREETING1"].ToString());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, EnvironmentPreviewValue>)result.Values).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<EnvironmentKeyOccurrence>)result.Values["A"].Occurrences).Clear());
    }

    [Fact]
    public void Empty_and_whole_line_comments_do_not_define_keys()
    {
        var preview = EnvironmentLiteralPreviewScanner.Scan(Snapshot("  \t\n# comment ${X}\n", "\t # comment\r\n", ""));
        Assert.True(preview.IsSupported);
        Assert.Empty(preview.Values);
    }

    [Theory]
    [InlineData("A='quoted'")]
    [InlineData("A=\"quoted\"")]
    [InlineData("A='multi\nline'")]
    [InlineData("A=\"multi\nline\"")]
    [InlineData("A=$VAR")]
    [InlineData("A=${VAR}")]
    [InlineData("A=${VAR:-default}")]
    [InlineData("A=${VAR-default}")]
    [InlineData("A=${VAR:?required}")]
    [InlineData("A=${VAR?required}")]
    [InlineData("A=${VAR:+alternate}")]
    [InlineData("A=${VAR+alternate}")]
    [InlineData("A=${VAR:-${OTHER}}")]
    [InlineData("A=$$escaped")]
    [InlineData("A=\\n")]
    [InlineData("A=first\\\nsecond")]
    [InlineData("A=x # comment")]
    [InlineData("A=x#literal")]
    [InlineData("A =x")]
    [InlineData(" A=x")]
    [InlineData("A= x")]
    [InlineData("A=x ")]
    [InlineData("A=x\ty")]
    [InlineData("export A=x")]
    [InlineData("A")]
    [InlineData("1A=x")]
    [InlineData("A-B=x")]
    [InlineData("雪=x")]
    [InlineData("=x")]
    [InlineData("A=one\nA=two")]
    [InlineData("\uFEFFA=x")]
    [InlineData("A=x\rB=y")]
    [InlineData("A=x\r")]
    [InlineData("A=secret\0value")]
    [InlineData("A=nonbreaking\u00A0space")]
    [InlineData("\u2028")]
    public void Unsupported_syntax_is_explicitly_refused_and_no_partial_effective_values_escape(string source)
    {
        var snapshot = Snapshot("KNOWN=literal", "# context\n" + source, "LATER=literal");
        var preview = EnvironmentLiteralPreviewScanner.Scan(snapshot);
        Assert.False(preview.IsSupported);
        Assert.Empty(preview.Values);
        Assert.NotEmpty(preview.Diagnostics);
        Assert.All(preview.Diagnostics, diagnostic => Assert.Equal(EnvironmentScope.Agent, diagnostic.Scope));
        Assert.All(preview.Diagnostics, diagnostic => Assert.True(diagnostic.Line >= 2));
        Assert.All(preview.Diagnostics, diagnostic => Assert.DoesNotContain("secret", diagnostic.Message));
        Assert.Equal(source, snapshot.AgentRawText[10..]);
    }

    [Fact]
    public void Required_inputs_are_not_silently_defaulted()
    {
        var service = new InMemoryDeploymentRevisionService(new(10, 1000, 1000));
        var revision = service.Save(Guid.NewGuid(), Guid.NewGuid(), 0, "compose.yaml", [ManagedFileSnapshot.Text("compose.yaml", "")], "");
        Assert.Throws<ArgumentNullException>(() => EnvironmentScopeSnapshot.Capture(null!, "", ""));
        Assert.Throws<ArgumentNullException>(() => EnvironmentScopeSnapshot.Capture(revision, null!, ""));
        Assert.Throws<ArgumentNullException>(() => EnvironmentScopeSnapshot.Capture(revision, "", null!));
        Assert.Throws<ArgumentNullException>(() => EnvironmentLiteralPreviewScanner.Scan(null!));
        Assert.Throws<EncoderFallbackException>(() => EnvironmentScopeSnapshot.Capture(revision, "A=\uD800", ""));
        Assert.Throws<EncoderFallbackException>(() => EnvironmentScopeSnapshot.Capture(revision, "", "A=\uD800"));
    }

    [Fact]
    public void Empty_deployment_assignments_override_inherited_literals_instead_of_falling_back()
    {
        var preview = EnvironmentLiteralPreviewScanner.Scan(Snapshot("A=global\nB=global", "A=agent\nB=", "A="));
        Assert.True(preview.IsSupported);
        Assert.Equal("", preview.Values["A"].Value);
        Assert.Equal(EnvironmentScope.Deployment, preview.Values["A"].Source);
        Assert.Equal("", preview.Values["B"].Value);
        Assert.Equal(EnvironmentScope.Agent, preview.Values["B"].Source);
    }

    private static EnvironmentScopeSnapshot Snapshot(string global, string agent, string deployment)
    {
        var service = new InMemoryDeploymentRevisionService(new(10, 10000, 10000));
        var revision = service.Save(Guid.NewGuid(), Guid.NewGuid(), 0, "compose.yaml", [ManagedFileSnapshot.Text("compose.yaml", "services: {}")], deployment);
        return EnvironmentScopeSnapshot.Capture(revision, global, agent);
    }
}
