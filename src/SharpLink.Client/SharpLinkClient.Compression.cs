namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private readonly CompressionSendPolicyState _requestCompressionPolicy;
    private ResponseCompressionPreferenceSnapshot _responseCompressionPreference =
        ResponseCompressionPreferenceSnapshot.InitialAllowed;

    public void UpdateRequestCompressionPolicy(SharpLinkCompressionSendPolicy policy)
    {
        lock (_stateGate)
        {
            var state = State;
            if (Volatile.Read(ref _stopStarted) != 0 ||
                state is SharpLinkConnectionState.Draining or
                    SharpLinkConnectionState.Stopped or
                    SharpLinkConnectionState.Faulted)
            {
                throw new InvalidOperationException(
                    $"Client state '{state}' does not accept request compression policy updates.");
            }
            _requestCompressionPolicy.Update(policy);
        }
    }

    public async ValueTask SetResponseCompressionPreferenceAsync(
        bool allowResponseCompression,
        CancellationToken cancellationToken = default)
    {
        var desired = PublishResponseCompressionPreference(allowResponseCompression);
        var cohort = CaptureResponseCompressionPreferenceCohort();

        for (var index = 0; index < cohort.Length; index++)
        {
            try
            {
                cohort[index].Session.ReconcileResponseCompressionPreference(desired);
            }
            catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ConnectionClosed)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        for (var index = 0; index < cohort.Length; index++)
        {
            await cohort[index].Session.WaitForResponseCompressionPreferenceAsync(
                desired.Generation,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private ResponseCompressionPreferenceSnapshot CaptureResponseCompressionPreference()
        => Volatile.Read(ref _responseCompressionPreference);

    private ResponseCompressionPreferenceSnapshot PublishResponseCompressionPreference(bool allowed)
    {
        lock (_stateGate)
        {
            var state = State;
            if (Volatile.Read(ref _stopStarted) != 0 ||
                state is SharpLinkConnectionState.Draining or
                    SharpLinkConnectionState.Stopped or
                    SharpLinkConnectionState.Faulted)
            {
                throw new InvalidOperationException(
                    $"Client state '{state}' does not accept response compression preference updates.");
            }

            var current = Volatile.Read(ref _responseCompressionPreference);
            if (current.Allowed == allowed)
                return current;
            if (current.Generation == ulong.MaxValue)
                throw new InvalidOperationException("The response compression preference generation is exhausted.");
            var candidate = new ResponseCompressionPreferenceSnapshot(current.Generation + 1, allowed);
            Volatile.Write(ref _responseCompressionPreference, candidate);
            return candidate;
        }
    }

    private ClientConnection[] CaptureResponseCompressionPreferenceCohort()
    {
        var ready = _cluster is null
            ? Volatile.Read(ref _readyConnections)
            : _cluster.CaptureReadyConnections();
        if (ready.Length == 0)
            return [];

        var eligible = new List<ClientConnection>(ready.Length);
        for (var index = 0; index < ready.Length; index++)
        {
            var connection = ready[index];
            if (connection.CanAcceptCalls && connection.Session.HasNegotiatedCompression)
                eligible.Add(connection);
        }
        return eligible.Count == 0 ? [] : eligible.ToArray();
    }
}
