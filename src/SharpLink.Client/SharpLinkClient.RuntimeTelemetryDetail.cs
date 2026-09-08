namespace SharpLink.Client;

internal sealed partial class SharpLinkClient : ISharpLinkTelemetryDetailRuntime
{
    private SharpLinkTelemetryDetailGeneration? _telemetryDetailGeneration;

    public SharpLinkTelemetryDetailPolicySnapshot GetTelemetryDetailPolicySnapshot()
    {
        var current = CaptureTelemetryDetailGeneration();
        return new SharpLinkTelemetryDetailPolicySnapshot(current.Generation, current.Mode);
    }

    public void UpdateTelemetryDetailPolicy(SharpLinkTelemetryDetailMode mode)
    {
        SharpLinkTelemetryDetailExtensions.Validate(mode);
        lock (_stateGate)
        {
            var state = State;
            if (Volatile.Read(ref _stopStarted) != 0 ||
                state is SharpLinkConnectionState.Draining or
                    SharpLinkConnectionState.Stopped or
                    SharpLinkConnectionState.Faulted)
            {
                throw new InvalidOperationException(
                    $"Telemetry detail policy cannot be updated while the client is {state}.");
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
}
