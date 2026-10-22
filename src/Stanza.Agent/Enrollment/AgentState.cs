namespace Stanza.Agent.Enrollment;

public sealed record AgentSnapshot(bool Ready, EnrollmentStatus Enrollment, string? AgentId)
{
    public bool CommandChannelAvailable { get; init; }
    public bool CanExecuteWork { get; init; }
}

public sealed class AgentState
{
    private AgentSnapshot snapshot = new(false, EnrollmentStatus.NotStarted, null);
    public AgentSnapshot Snapshot => Volatile.Read(ref snapshot);
    public void Set(AgentSnapshot value) => Volatile.Write(ref snapshot, value);
}
