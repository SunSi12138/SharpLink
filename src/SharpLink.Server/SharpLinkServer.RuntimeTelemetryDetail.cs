namespace SharpLink.Server;

internal sealed partial class SharpLinkServer : ISharpLinkTelemetryDetailRuntime
{
    private readonly Lock _telemetryDetailGate = new();
    private SharpLinkTelemetryDetailGeneration? _telemetryDetailGeneration;

    public SharpLinkTelemetryDetailPolicySnapshot GetTelemetryDetailPolicySnapshot()
    {
        var current = CaptureTelemetryDetailGeneration();
        return new SharpLinkTelemetryDetailPolicySnapshot(current.Generation, current.Mode);
    }

    public void UpdateTelemetryDetailPolicy(SharpLinkTelemetryDetailMode mode)
    {
        SharpLinkTelemetryDetailExtensions.Validate(mode);
        lock (_telemetryDetailGate)
        {
            var state = (ServerState)Volatile.Read(ref _state);
            if (state is ServerState.Draining or ServerState.Stopped or ServerState.Faulted)
            {
                throw new InvalidOperationException(
                    $"Telemetry detail policy cannot be updated while the server is {state}.");
            }

            var current = CaptureTelemetryDetailGeneration();
            if (current.Mode == mode)
                return;
            if (current.Generation == ulong.MaxValue)
                throw new InvalidOperationException("Telemetry detail policy generation is exhausted.");

            Volatile.Write(
                ref _telemetryDetailGeneration,
                new SharpLinkTelemetryDetailGeneration(current.Generation + 1, mode));
        }
    }

    private SharpLinkTelemetryDetailGeneration CaptureTelemetryDetailGeneration()
    {
        var current = Volatile.Read(ref _telemetryDetailGeneration);
        if (current is not null)
            return current;

        var initial = new SharpLinkTelemetryDetailGeneration(
            generation: 0,
            SharpLinkTelemetryDetailMode.Detailed);
        return Interlocked.CompareExchange(ref _telemetryDetailGeneration, initial, null) ?? initial;
    }

    /// <summary>
    /// Starts server telemetry from the facts the RPC already resolved, so an enabled listener never
    /// forces a descriptor projection onto the dispatch path.
    /// </summary>
    private SharpLinkTelemetry.CallScope StartServerTelemetryCall(
        long contractId,
        long methodId,
        RpcMethodShape shape,
        long requestId)
    {
        var detail = CaptureTelemetryDetailGeneration().Mode;
        return SharpLinkTelemetry.StartServerCall(
            contractId,
            methodId,
            shape,
            detail == SharpLinkTelemetryDetailMode.Detailed ? requestId : 0);
    }
}
