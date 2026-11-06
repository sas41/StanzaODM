namespace Stanza.Core.Contracts;

public sealed record ExecutionSlot(Guid DeploymentId, string Kind);

public static class ExecutionScheduling
{
    public static bool IsTransientDiscovery(string kind) => kind is "git-branches" or "git-action-branches" or "registry-tags" or "resolve-action-inputs" or "registry-check";
    public static bool IsDiscovery(string kind) => IsTransientDiscovery(kind) || kind is "image-digests" or "environment-preview" or "status" or "logs";
    public static bool IsExclusive(string kind) => kind is "docker-script" or "host-exec";
    public static bool IsValid(AgentRuntimePolicy policy) => policy.MaximumParallelOperations is > 0 and <= 64 &&
        policy.MaximumParallelDiscovery is > 0 and <= 64 && policy.DiscoveryTimeoutSeconds is > 0 and <= 604800;

    public static bool CanAdmit(AgentRuntimePolicy policy, ExecutionSlot requested, IEnumerable<ExecutionSlot> active)
    {
        if (!IsValid(policy)) return false;
        var slots = active.ToArray();
        if (IsDiscovery(requested.Kind)) return slots.Count(s => IsDiscovery(s.Kind)) < policy.MaximumParallelDiscovery;
        var workloads = slots.Where(s => !IsDiscovery(s.Kind)).ToArray();
        return workloads.Length < policy.MaximumParallelOperations &&
            !workloads.Any(s => IsExclusive(s.Kind) || IsExclusive(requested.Kind) ||
                requested.DeploymentId != Guid.Empty && s.DeploymentId == requested.DeploymentId);
    }
}
