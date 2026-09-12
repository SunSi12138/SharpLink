namespace SharpLink.Server;

internal sealed record ServerShutdownPlan
{
    internal static ServerShutdownPlan Default { get; } = new(TimeSpan.FromSeconds(5));

    internal ServerShutdownPlan(TimeSpan cleanupBudget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cleanupBudget, TimeSpan.Zero);
        CleanupBudget = cleanupBudget;
    }

    internal TimeSpan CleanupBudget { get; }
}
