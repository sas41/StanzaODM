using System.Text;
using System.Text.Json;
using Stanza.ControlPlane.Terminals;

namespace Stanza.Tests.Terminals;

public sealed class TerminalProtocolTests
{
    public static TerminalMessage Open => new("open", Service: "web", Executable: "/bin/sh", Arguments: [], Columns: 80, Rows: 24);

    [Fact]
    public void ExecutionPreservesArgumentBoundariesWithoutShellConcatenation()
    {
        var execution = TerminalProtocol.Execution(Open with { Arguments = ["-c", "printf '%s' '$SECRET;$(touch /no)'"], User = "1000", WorkingDirectory = "/app" });
        Assert.Equal("/bin/sh", execution.Executable);
        Assert.Equal("printf '%s' '$SECRET;$(touch /no)'", execution.Arguments[1]);
        Assert.Equal("1000", execution.User);
        Assert.Equal("/app", execution.WorkingDirectory);
        Assert.Null(execution.Environment);
        Assert.DoesNotContain("SECRET", execution.ToString());
    }

    [Fact]
    public void ServiceAndContainerAreExclusiveAndNeverOptional()
    {
        Assert.Throws<TerminalProtocolException>(() => TerminalProtocol.Execution(Open with { Service = null }));
        Assert.Throws<TerminalProtocolException>(() => TerminalProtocol.Execution(Open with { Container = "container" }));
        Assert.Equal("container", TerminalProtocol.Execution(Open with { Service = null, Container = "container" }).Container);
    }

    [Fact]
    public void UnsafeAndUnboundedExecutionFieldsAreRejected()
    {
        TerminalMessage[] invalid = [Open with { Type = "input" }, Open with { Data = "secret" },
            Open with { Executable = "-exec" }, Open with { Executable = "" }, Open with { Executable = "sh\0" },
            Open with { Service = "-host" }, Open with { Service = "web;other" }, Open with { Service = "/host" },
            Open with { Service = "web other" }, Open with { Container = "-host", Service = null },
            Open with { User = "-root" }, Open with { WorkingDirectory = "relative" },
            Open with { Arguments = null }, Open with { Arguments = Enumerable.Repeat("arg", 65).ToArray() },
            Open with { Arguments = [new string('a', 4097)] }, Open with { Arguments = ["arg\0"] },
            Open with { Arguments = [new string('a', 4096), new string('a', 4096), "a"] },
            Open with { Service = new string('a', 129) }, Open with { Rows = 301 }, Open with { Columns = 501 }];
        foreach (var message in invalid) Assert.Throws<TerminalProtocolException>(() => TerminalProtocol.Execution(message));
    }

    [Theory]
    [InlineData(2, 1, true)]
    [InlineData(500, 300, true)]
    [InlineData(1, 24, false)]
    [InlineData(80, 0, false)]
    [InlineData(501, 24, false)]
    [InlineData(80, 301, false)]
    public void ResizeBoundsAreExplicit(int columns, int rows, bool expected) => Assert.Equal(expected, TerminalProtocol.ValidDimensions(columns, rows));

    [Fact]
    public void BinaryInputUsesBoundedBase64AndSensitiveRecordsAreRedacted()
    {
        var bytes = Enumerable.Repeat((byte)255, 65536).ToArray();
        var message = new TerminalMessage("input", Data: Convert.ToBase64String(bytes));
        Assert.Equal(bytes, TerminalProtocol.Input(message));
        Assert.Throws<TerminalProtocolException>(() => TerminalProtocol.Input(message with { Data = Convert.ToBase64String(new byte[65537]) }));
        Assert.Throws<TerminalProtocolException>(() => TerminalProtocol.Input(message with { Data = "" }));
        Assert.Throws<FormatException>(() => TerminalProtocol.Input(message with { Data = "invalid!" }));
        Assert.DoesNotContain(message.Data!, message.ToString());
        Assert.DoesNotContain("secret", new TerminalOutput(Encoding.UTF8.GetBytes("secret")).ToString());
    }

    [Theory]
    [InlineData("{\"type\":\"open\",\"environment\":{\"SECRET\":\"value\"}}")]
    [InlineData("null")]
    [InlineData("not-json")]
    public void MalformedOrUnknownProtocolFieldsAreRejected(string json) =>
        Assert.Throws<JsonException>(() => TerminalProtocol.Parse(Encoding.UTF8.GetBytes(json)));
}
