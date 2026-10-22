using System.Collections.ObjectModel;
using System.Text;

namespace Stanza.Core.Deployments;

/// <summary>A single manually authored Compose file and immutable supporting inputs; not Compose validation.</summary>
public sealed class DeploymentRevision
{
    internal DeploymentRevision(Guid deploymentId, Guid targetAgentId, long number,
        DeploymentPath composePath, ManagedFileSnapshot[] files, GitSourceWorkspace[] sources,
        string environmentText)
    {
        DeploymentId = deploymentId;
        TargetAgentId = targetAgentId;
        Number = number;
        ComposePath = composePath;
        Files = Array.AsReadOnly(files);
        Sources = Array.AsReadOnly(sources);
        EnvironmentText = environmentText;
    }

    public Guid DeploymentId { get; }
    public Guid TargetAgentId { get; }
    public long Number { get; }
    public DeploymentPath ComposePath { get; }
    public ReadOnlyCollection<ManagedFileSnapshot> Files { get; }
    public ReadOnlyCollection<GitSourceWorkspace> Sources { get; }
    public string EnvironmentText { get; }
    public string ComposeText => Files.Single(file => file.Path == ComposePath).GetText();
    public override string ToString() => $"Deployment {DeploymentId}, revision {Number}";
}

/// <summary>Thread-safe in-memory revision foundation only. No durability, authorization, or runtime execution.</summary>
public sealed class InMemoryDeploymentRevisionService
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, List<DeploymentRevision>> revisions = [];
    private readonly ManagedFileLimits limits;

    public InMemoryDeploymentRevisionService(ManagedFileLimits limits) =>
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));

    /// <param name="expectedRevision">Zero creates a deployment; later saves require its current revision.</param>
    public DeploymentRevision Save(Guid deploymentId, Guid targetAgentId, long expectedRevision,
        string composePath, IEnumerable<ManagedFileSnapshot> files, string environmentText,
        IEnumerable<GitSourceWorkspace>? sources = null)
    {
        if (deploymentId == Guid.Empty) throw new ArgumentException("A deployment identity is required.", nameof(deploymentId));
        if (targetAgentId == Guid.Empty) throw new ArgumentException("A target Agent is required.", nameof(targetAgentId));
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(environmentText);
        _ = new UTF8Encoding(false, true).GetByteCount(environmentText);
        var path = DeploymentPath.Parse(composePath);
        var fileSnapshot = files.ToArray();
        var sourceSnapshot = sources?.ToArray() ?? [];
        DeploymentWorkspaceValidator.Validate(fileSnapshot, sourceSnapshot, limits);
        if (!fileSnapshot.Any(file => file.Path == path && file.Kind == ManagedEntryKind.Text))
            throw new ArgumentException("The designated Compose file must be a managed text file with exact path casing.", nameof(composePath));

        lock (gate)
        {
            revisions.TryGetValue(deploymentId, out var history);
            var current = history?.Count ?? 0;
            if (expectedRevision != current)
                throw new InvalidOperationException("Revision conflict: reload before saving.");
            var revision = new DeploymentRevision(deploymentId, targetAgentId, checked(current + 1),
                path, fileSnapshot, sourceSnapshot, environmentText);
            if (history is null) revisions.Add(deploymentId, history = []);
            history.Add(revision);
            return revision;
        }
    }

    public DeploymentRevision Get(Guid deploymentId, long? number = null)
    {
        lock (gate)
        {
            if (!revisions.TryGetValue(deploymentId, out var history))
                throw new KeyNotFoundException("Deployment does not exist.");
            var requested = number ?? history.Count;
            if (requested <= 0 || requested > history.Count)
                throw new ArgumentOutOfRangeException(nameof(number));
            return history[checked((int)requested - 1)];
        }
    }
}
