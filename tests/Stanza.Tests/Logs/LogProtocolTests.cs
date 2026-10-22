using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Stanza.ControlPlane.Audit;
using Stanza.ControlPlane.Live;
using Stanza.ControlPlane.Logs;
using Stanza.ControlPlane.Terminals;

namespace Stanza.Tests.Logs;

public sealed class LogProtocolTests
{
    [Theory]
    [InlineData("service")]
    [InlineData("container")]
    public void OpenCanOnlyRequestReadOnlyLogsForOneTypedTarget(string kind)
    {
        var message = kind == "service" ? new LogMessage("open", Service: "app-service_1.2") : new("open", Container: "012345abcdef");
        var execution = LogProtocol.Execution(message);
        Assert.Equal("logs", execution.Mode);
        Assert.Equal("logs", execution.Executable);
        Assert.Empty(execution.Arguments);
        Assert.Equal(message.Service, execution.Service);
        Assert.Equal(message.Container, execution.Container);
        Assert.Null(execution.User);
        Assert.Null(execution.WorkingDirectory);
        Assert.Null(execution.Environment);
        Assert.DoesNotContain("app-service", message.ToString());
    }

    [Theory]
    [InlineData("-")]
    [InlineData("-host")]
    [InlineData("/other")]
    [InlineData("app;other")]
    [InlineData("app other")]
    [InlineData("app\0")]
    [InlineData("app\n")]
    [InlineData("é")]
    public void InvalidTargetIdentifiersAreRejected(string target)
    {
        Assert.Throws<LogProtocolException>(() => LogProtocol.Execution(new("open", Service: target)));
        Assert.Throws<LogProtocolException>(() => LogProtocol.Execution(new("open", Container: target)));
    }

    [Fact]
    public void MissingAmbiguousOversizedTargetsAndOtherMessageTypesCannotExecute()
    {
        LogMessage[] invalid = [new("open"), new("open", Service: "", Container: ""), new("open", Service: "web", Container: "other"),
            new("open", Service: new string('a', 129)), new("input", Service: "web"), new("resize", Container: "other"), new("close", Service: "web")];
        foreach (var message in invalid) Assert.Throws<LogProtocolException>(() => LogProtocol.Execution(message));
        Assert.Equal(128, LogProtocol.Execution(new("open", Service: new string('a', 128))).Service!.Length);
        Assert.True(LogProtocol.IsClose(new("close")));
        Assert.False(LogProtocol.IsClose(new("close", Container: "web")));
        Assert.False(LogProtocol.IsClose(new("input")));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("not-json")]
    [InlineData("{\"type\":\"open\",\"executable\":\"/bin/sh\"}")]
    [InlineData("{\"type\":\"open\",\"mode\":\"exec\"}")]
    [InlineData("{\"type\":\"input\",\"data\":\"secret\"}")]
    [InlineData("{\"type\":\"resize\",\"columns\":80,\"rows\":24}")]
    public void UnknownOrMalformedFieldsAreRejected(string json) => Assert.Throws<JsonException>(() => LogProtocol.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void RegistrationIsIdempotentSharesLeasesAndDoesNotFakeTheTransport()
    {
        var services = new ServiceCollection();
        services.AddStanzaTerminals().AddStanzaLogs().AddStanzaLogs();
        Assert.Single(services, service => service.ServiceType == typeof(LiveConnectionLeases));
        Assert.Single(services, service => service.ServiceType == typeof(AuditService));
        Assert.Single(services, service => service.ServiceType == typeof(LogAccess));
        Assert.DoesNotContain(services, service => service.ServiceType == typeof(IAgentTerminalTransport));
    }
}
