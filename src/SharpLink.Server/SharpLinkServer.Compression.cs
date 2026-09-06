namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private readonly CompressionSendPolicyState _responseCompressionPolicy;

    public void UpdateResponseCompressionPolicy(SharpLinkCompressionSendPolicy policy)
    {
        lock (_stateGate)
        {
            if (_lifecycle.HasStopStarted ||
                CurrentState is ServerState.Draining or ServerState.Stopped or ServerState.Faulted)
            {
                throw new InvalidOperationException(
                    $"Server state '{CurrentState}' does not accept response compression policy updates.");
            }
            _responseCompressionPolicy.Update(policy);
        }
    }
}
