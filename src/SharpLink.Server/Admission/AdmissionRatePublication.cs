namespace SharpLink.Server;

/// <summary>
/// Completes publication-only rate state after the immutable AdmissionProgram pointer is visible.
/// Candidate construction and update-plan commit may prepare a DynamicFixedWindow successor, but
/// only this hook is allowed to make its queued/current-window target authoritative.
/// </summary>
internal static class AdmissionRatePublication
{
    internal static void PublishTargets(SharpLinkAdmissionController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        foreach (var binding in controller.RuleStateBindings)
            binding.RateState?.OnPublished();
    }
}
