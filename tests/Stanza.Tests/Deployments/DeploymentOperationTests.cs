using Stanza.Core.Deployments;

namespace Stanza.Tests.Deployments;

public sealed class DeploymentOperationTests
{
    [Fact]
    public void Every_state_pair_is_checked_against_the_explicit_transition_matrix()
    {
        var allowed = new Dictionary<DeploymentOperationState, DeploymentOperationState[]>
        {
            [DeploymentOperationState.Queued] = [DeploymentOperationState.Validating, DeploymentOperationState.Failed, DeploymentOperationState.Cancelled],
            [DeploymentOperationState.Validating] = [DeploymentOperationState.Preparing, DeploymentOperationState.Failed, DeploymentOperationState.Cancelled],
            [DeploymentOperationState.Preparing] = [DeploymentOperationState.BuildingOrPulling, DeploymentOperationState.Applying, DeploymentOperationState.Failed, DeploymentOperationState.Cancelled, DeploymentOperationState.UnknownRequiresReconcile],
            [DeploymentOperationState.BuildingOrPulling] = [DeploymentOperationState.Applying, DeploymentOperationState.Failed, DeploymentOperationState.UnknownRequiresReconcile],
            [DeploymentOperationState.Applying] = [DeploymentOperationState.Checking, DeploymentOperationState.Failed, DeploymentOperationState.UnknownRequiresReconcile],
            [DeploymentOperationState.Checking] = [DeploymentOperationState.Succeeded, DeploymentOperationState.Failed, DeploymentOperationState.UnknownRequiresReconcile],
            [DeploymentOperationState.UnknownRequiresReconcile] = [DeploymentOperationState.Succeeded, DeploymentOperationState.Failed, DeploymentOperationState.Cancelled],
            [DeploymentOperationState.Succeeded] = [],
            [DeploymentOperationState.Failed] = [],
            [DeploymentOperationState.Cancelled] = []
        };
        foreach (var from in Enum.GetValues<DeploymentOperationState>())
        foreach (var to in Enum.GetValues<DeploymentOperationState>())
        {
            var expected = allowed[from].Contains(to);
            Assert.Equal(expected, DeploymentOperationTransitions.CanTransition(from, to));
            if (expected)
                Assert.Equal(to, DeploymentOperationTransitions.Transition(from, to, reconciled: true));
            else
                Assert.Throws<InvalidOperationException>(() => DeploymentOperationTransitions.Transition(from, to, reconciled: true));
        }
    }

    [Fact]
    public void Uncertain_outcomes_require_explicit_reconciliation_and_cannot_restart_steps()
    {
        foreach (var terminal in new[] { DeploymentOperationState.Succeeded, DeploymentOperationState.Failed, DeploymentOperationState.Cancelled })
        {
            Assert.Throws<InvalidOperationException>(() => DeploymentOperationTransitions.Transition(DeploymentOperationState.UnknownRequiresReconcile, terminal));
            Assert.Equal(terminal, DeploymentOperationTransitions.Transition(DeploymentOperationState.UnknownRequiresReconcile, terminal, reconciled: true));
            Assert.True(DeploymentOperationTransitions.IsTerminal(terminal));
        }
        Assert.False(DeploymentOperationTransitions.IsTerminal(DeploymentOperationState.UnknownRequiresReconcile));
        Assert.False(DeploymentOperationTransitions.IsTerminal(DeploymentOperationState.Applying));
    }

    [Fact]
    public void Undefined_enum_values_never_transition()
    {
        Assert.False(DeploymentOperationTransitions.CanTransition((DeploymentOperationState)999, DeploymentOperationState.Failed));
        Assert.False(DeploymentOperationTransitions.CanTransition(DeploymentOperationState.Queued, (DeploymentOperationState)999));
        Assert.Throws<InvalidOperationException>(() => DeploymentOperationTransitions.Transition((DeploymentOperationState)999, DeploymentOperationState.Failed));
    }
}
