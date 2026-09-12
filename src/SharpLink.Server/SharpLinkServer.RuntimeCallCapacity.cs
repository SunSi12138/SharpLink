namespace SharpLink.Server;

internal sealed partial class SharpLinkServer : ISharpLinkServerCallCapacityRuntimeControl
{
    void ISharpLinkServerCallCapacityRuntimeControl.UpdateCallCapacity(
        int maxConcurrentCallsPerConnection,
        int maxConcurrentCallsPerServer)
    {
        var candidate = ServerCallCapacityLimits.CreateValidated(
            maxConcurrentCallsPerConnection,
            maxConcurrentCallsPerServer);

        lock (_stateGate)
        {
            if (_lifecycle.HasStopStarted ||
                CurrentState is ServerState.Draining or ServerState.Stopped or ServerState.Faulted)
            {
                throw new InvalidOperationException(
                    "Call-capacity publication is sealed because the server is stopping.");
            }

            _callAdmission.UpdateLimits(candidate);
        }
    }
}
