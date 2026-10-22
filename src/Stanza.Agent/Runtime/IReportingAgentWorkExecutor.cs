namespace Stanza.Agent.Runtime;

public interface IReportingAgentWorkExecutor
{
    event Func<Guid, string, Task>? Progress;
}
