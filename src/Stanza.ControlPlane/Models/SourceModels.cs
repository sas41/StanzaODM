using Stanza.ControlPlane.Sources;

namespace Stanza.ControlPlane.Models;

public sealed record CredentialListModel(IReadOnlyList<CredentialSummary> Credentials, IReadOnlyList<SourceAgentSummary> Agents, bool CanCreate);
public sealed class CredentialEditModel
{
    public Guid? Id { get; set; }
    public long ExpectedVersion { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "git";
    public string Endpoint { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public override string ToString() => "CredentialEditModel { [REDACTED] }";
}
public sealed record SourceDetailsModel(DeploymentSourceSummary Deployment, IReadOnlyList<CredentialSummary> Credentials);
public sealed class GitSourceEditModel
{
    public Guid DeploymentId { get; set; }
    public Guid? Id { get; set; }
    public long ExpectedVersion { get; set; }
    public string Url { get; set; } = "";
    public string Reference { get; set; } = "main";
    public string Destination { get; set; } = "sources/app";
    public Guid? CredentialId { get; set; }
    public IReadOnlyList<CredentialSummary> Credentials { get; set; } = [];
}
public sealed record BranchPartialModel(Guid DeploymentId, Guid SourceId, BranchPage? Page, string? Error = null);
