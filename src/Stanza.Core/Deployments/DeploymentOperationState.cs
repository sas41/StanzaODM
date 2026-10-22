namespace Stanza.Core.Deployments;

public enum DeploymentOperationState
{
    Queued,
    Validating,
    Preparing,
    BuildingOrPulling,
    Applying,
    Checking,
    Succeeded,
    Failed,
    Cancelled,
    UnknownRequiresReconcile
}

/// <summary>Pure transition policy, not a durable executor or a promise of cancellation/rollback safety.</summary>
public static class DeploymentOperationTransitions
{
    public static bool IsTerminal(DeploymentOperationState state) =>
        state is DeploymentOperationState.Succeeded or DeploymentOperationState.Failed or DeploymentOperationState.Cancelled;

    public static bool CanTransition(DeploymentOperationState from, DeploymentOperationState to)
    {
        if (!Enum.IsDefined(from) || !Enum.IsDefined(to) || from == to || IsTerminal(from)) return false;
        if (from == DeploymentOperationState.UnknownRequiresReconcile)
            // Only separately verified terminal observations may resolve uncertainty; never blindly retry a step.
            return IsTerminal(to);
        if (to == DeploymentOperationState.Failed) return true;
        if (to == DeploymentOperationState.Cancelled)
            return from is DeploymentOperationState.Queued or DeploymentOperationState.Validating or DeploymentOperationState.Preparing;
        if (to == DeploymentOperationState.UnknownRequiresReconcile)
            return from is DeploymentOperationState.Preparing or DeploymentOperationState.BuildingOrPulling or
                DeploymentOperationState.Applying or DeploymentOperationState.Checking;

        return (from, to) switch
        {
            (DeploymentOperationState.Queued, DeploymentOperationState.Validating) => true,
            (DeploymentOperationState.Validating, DeploymentOperationState.Preparing) => true,
            (DeploymentOperationState.Preparing, DeploymentOperationState.BuildingOrPulling) => true,
            (DeploymentOperationState.Preparing, DeploymentOperationState.Applying) => true,
            (DeploymentOperationState.BuildingOrPulling, DeploymentOperationState.Applying) => true,
            (DeploymentOperationState.Applying, DeploymentOperationState.Checking) => true,
            (DeploymentOperationState.Checking, DeploymentOperationState.Succeeded) => true,
            _ => false
        };
    }

    /// <param name="reconciled">Required when resolving an uncertain outcome, after external state inspection.</param>
    public static DeploymentOperationState Transition(DeploymentOperationState from, DeploymentOperationState to,
        bool reconciled = false)
    {
        if (!CanTransition(from, to) || (from == DeploymentOperationState.UnknownRequiresReconcile && !reconciled))
            throw new InvalidOperationException("Operation transition is not allowed or requires reconciliation.");
        return to;
    }
}
