using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Stanza.Agent;
using Stanza.Agent.Runtime;

namespace Stanza.Tests.Agent;

public sealed class AgentOptionsTests
{
    [Theory]
    [InlineData("edge-agent-one", true)]
    [InlineData("  Edge agent one  ", true)]
    [InlineData("", true)]
    [InlineData("edge\nagent", false)]
    [InlineData("edge\0agent", false)]
    [InlineData(null, false)]
    public void Friendly_name_is_validated_without_reflecting_its_contents(string? name, bool valid)
    {
        var options = ValidOptions(); options.Name = name!;
        var result = new AgentOptionsValidator(new TestEnvironment("Production")).Validate(null, options);
        Assert.Equal(valid, result.Succeeded);
        Assert.DoesNotContain("edge\nagent", string.Join(";", result.Failures ?? []));
        if (valid) Assert.Equal(string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name.Trim(), options.RegistrationName);
    }

    [Fact]
    public void Friendly_name_length_is_bounded()
    {
        var options = ValidOptions(); options.Name = new('a', 253);
        Assert.True(new AgentOptionsValidator(new TestEnvironment("Production")).Validate(null, options).Succeeded);
        options.Name += "a";
        Assert.True(new AgentOptionsValidator(new TestEnvironment("Production")).Validate(null, options).Failed);
    }

    [Theory]
    [InlineData("https://control.example", false, "Production", true)]
    [InlineData("http://control.example", true, "Development", false)]
    [InlineData("http://localhost:5000", false, "Development", false)]
    [InlineData("http://localhost:5000", true, "Production", false)]
    [InlineData("http://localhost:5000", true, "Development", true)]
    [InlineData("http://127.0.0.1:5000", true, "Development", true)]
    [InlineData("http://[::1]:5000", true, "Development", true)]
    [InlineData("https://secret@control.example", false, "Production", false)]
    [InlineData("https://control.example?token=secret", false, "Production", false)]
    [InlineData("https://control.example/#secret", false, "Production", false)]
    [InlineData("https://control.example/path", false, "Production", false)]
    [InlineData("file:///private", false, "Production", false)]
    [InlineData("", false, "Production", false)]
    public void EnforcesTransportPolicy(string url, bool allowHttp, string environment, bool succeeds)
    {
        var options = ValidOptions();
        options.ControlPlaneUrl = url;
        options.AllowLoopbackHttp = allowHttp;
        var result = new AgentOptionsValidator(new TestEnvironment(environment)).Validate(null, options);
        Assert.Equal(succeeds, result.Succeeded);
        Assert.DoesNotContain("secret", string.Join(";", result.Failures ?? []));
    }

    [Theory]
    [InlineData("http://192.0.2.10:8888", false, false)]
    [InlineData("http://192.0.2.10:8888", true, true)]
    [InlineData("http://control.example.test", true, true)]
    [InlineData("https://control.example.test", false, true)]
    [InlineData("https://control.example.test", true, true)]
    [InlineData("http://secret@control.example.test", true, false)]
    [InlineData("http://control.example.test/path", true, false)]
    [InlineData("http://control.example.test?token=secret", true, false)]
    [InlineData("http://control.example.test/#secret", true, false)]
    [InlineData("file:///secret", true, false)]
    public void Production_cleartext_transport_requires_explicit_opt_in_and_still_validates_origins(string url, bool enabled, bool succeeds)
    {
        var options = ValidOptions(); options.ControlPlaneUrl = url; options.AllowInsecureHttp = enabled;
        var result = new AgentOptionsValidator(new TestEnvironment("Production")).Validate(null, options);
        Assert.Equal(succeeds, result.Succeeded);
        Assert.DoesNotContain("secret", string.Join(";", result.Failures ?? []));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/data")]
    [InlineData("/")]
    [InlineData("/tmp/opencode/invalid\0path")]
    public void RequiresDedicatedAbsoluteDataRoot(string root)
    {
        var options = ValidOptions();
        options.PersistentDataRoot = root;
        Assert.True(new AgentOptionsValidator(new TestEnvironment("Production")).Validate(null, options).Failed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("secret")]
    [InlineData("secret\nsecretsecretsecretsecretsecret")]
    public void RejectsInvalidTokenWithoutDisclosingIt(string token)
    {
        var options = ValidOptions();
        options.EnrollmentToken = token;
        var result = new AgentOptionsValidator(new TestEnvironment("Production")).Validate(null, options);
        Assert.True(result.Failed);
        Assert.DoesNotContain("secret", string.Join(";", result.Failures));
    }

    [Theory]
    [InlineData("unix:///var/run/docker.sock", true)]
    [InlineData("tcp://127.0.0.1:2375", false)]
    [InlineData("https://docker.example", false)]
    [InlineData("unix://remote/socket", false)]
    [InlineData("unix:///", false)]
    [InlineData("unix:///socket?secret", false)]
    [InlineData("unix:relative", false)]
    [InlineData("unix:/var/run/docker.sock", false)]
    public void DockerEndpointIsExplicitUnixSocketOnly(string endpoint, bool valid) =>
        Assert.Equal(valid, DockerRuntimeOptions.IsValid(new() { Endpoint = endpoint }));

    [Fact]
    public void ExecutionIsDisabledByDefault() => Assert.False(new DockerRuntimeOptions().EnableExecution);

    [Fact]
    public void DockerProcessLimitsAreValidated()
    {
        Assert.False(DockerRuntimeOptions.IsValid(new() { ExecutablePath = "docker" }));
        Assert.False(DockerRuntimeOptions.IsValid(new() { ExecutablePath = "/usr/bin/docker\0" }));
        Assert.False(DockerRuntimeOptions.IsValid(new() { OperationTimeoutSeconds = 0 }));
        Assert.False(DockerRuntimeOptions.IsValid(new() { OperationTimeoutSeconds = 3601 }));
        Assert.False(DockerRuntimeOptions.IsValid(new() { MaxOutputCharacters = 1023 }));
        Assert.False(DockerRuntimeOptions.IsValid(new() { MaxOutputCharacters = 1048577 }));
    }

    internal static AgentOptions ValidOptions() => new()
    {
        PersistentDataRoot = "/tmp/opencode/agent-data",
        ControlPlaneUrl = "https://control.example",
        EnrollmentToken = new string('s', 32)
    };

    private sealed class TestEnvironment(string environment) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environment;
        public string ApplicationName { get; set; } = "Stanza.Agent";
        public string ContentRootPath { get; set; } = "/tmp/opencode";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
