namespace Stanza.ControlPlane.Sources;

public sealed class SourceAccessException() : Exception("You do not have permission for this source or credential.");
public sealed class SourceException(string message) : Exception(message);
public sealed record CredentialSummary(Guid Id, string Name, string Kind, string Endpoint, long Version);
public sealed record CredentialSecret(string Username, string Password)
{
    public override string ToString() => "CredentialSecret { [REDACTED] }";
}
public sealed record CredentialInput(string Name, string Kind, string Endpoint, string Username, string Password)
{
    public override string ToString() => "CredentialInput { [REDACTED] }";
}
public sealed record GitSourceSummary(Guid Id, Guid DeploymentId, string Url, string Reference, string Destination,
    Guid? CredentialId, int Position, long Version);
public sealed record GitSourceInput(string Url, string Reference, string Destination, Guid? CredentialId);
public sealed record DeploymentSourceSummary(Guid DeploymentId, string Name, Guid? TargetAgentId,
    IReadOnlyList<GitSourceSummary> Git, IReadOnlyList<Guid> RegistryCredentialIds, bool CanManage);
public sealed record BranchPage(IReadOnlyList<string> References, string Selected, bool SelectedMissing,
    int Page, bool HasMore, string Search);
public sealed record SourceAgentSummary(Guid Id, string Name);
