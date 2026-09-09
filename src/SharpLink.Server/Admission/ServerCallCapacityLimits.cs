namespace SharpLink.Server;

/// <summary>
/// Immutable publication unit for the two independent hard call-capacity bounds. A request reads
/// one instance before acquiring either scope so a runtime update cannot expose a mixed pair.
/// </summary>
internal sealed class ServerCallCapacityLimits
{
    private ServerCallCapacityLimits(
        int maxConcurrentCallsPerConnection,
        int maxConcurrentCallsPerServer)
    {
        MaxConcurrentCallsPerConnection = maxConcurrentCallsPerConnection;
        MaxConcurrentCallsPerServer = maxConcurrentCallsPerServer;
    }

    internal int MaxConcurrentCallsPerConnection { get; }

    internal int MaxConcurrentCallsPerServer { get; }

    internal static ServerCallCapacityLimits CreateValidated(
        int maxConcurrentCallsPerConnection,
        int maxConcurrentCallsPerServer)
    {
        if (maxConcurrentCallsPerConnection is < 1 or >
            SharpLinkFlowControlOptions.MaximumConcurrentCallsPerConnection)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrentCallsPerConnection),
                maxConcurrentCallsPerConnection,
                $"Call capacity must be between 1 and {SharpLinkFlowControlOptions.MaximumConcurrentCallsPerConnection}.");
        }

        if (maxConcurrentCallsPerServer is < 1 or >
            SharpLinkFlowControlOptions.MaximumConcurrentCallsPerServer)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrentCallsPerServer),
                maxConcurrentCallsPerServer,
                $"Call capacity must be between 1 and {SharpLinkFlowControlOptions.MaximumConcurrentCallsPerServer}.");
        }

        return new ServerCallCapacityLimits(
            maxConcurrentCallsPerConnection,
            maxConcurrentCallsPerServer);
    }
}
