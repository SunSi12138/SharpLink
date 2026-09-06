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

    public ValueTask SetResponseCompressionPreferenceAsync(
        bool allowResponseCompression,
        CancellationToken cancellationToken = default)
    {
        var desired = PublishResponseCompressionPreference(allowResponseCompression);
        var cohort = CaptureResponseCompressionPreferenceCohort();
        return ApplyResponseCompressionPreferenceToCohortAsync(cohort, desired, cancellationToken);
    }

    internal static async ValueTask ApplyResponseCompressionPreferenceToCohortAsync(
        RpcSession[] cohort,
        ResponseCompressionPreferenceSnapshot desired,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cohort);
        List<Exception>? failures = null;
        var failed = cohort.Length == 0 ? Array.Empty<bool>() : new bool[cohort.Length];

        for (var index = 0; index < cohort.Length; index++)
        {
            try
            {
                cohort[index].ReconcileResponseCompressionPreference(desired);
            }
            catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ConnectionClosed)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                failed[index] = true;
                (failures ??= []).Add(exception);
            }
        }

        for (var index = 0; index < cohort.Length; index++)
        {
            if (failed[index])
                continue;
            try
            {
                await cohort[index].WaitForResponseCompressionPreferenceAsync(
                    desired.Generation,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ConnectionClosed)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        ThrowResponseCompressionPreferenceFailures(failures);
    }

    private ResponseCompressionPreferenceSnapshot CaptureResponseCompressionPreference()
        => Volatile.Read(ref _responseCompressionPreference);

    private void ReconcileResponseCompressionPreferenceAfterReadyPublication(RpcSession session)
    {
        if (!session.HasNegotiatedCompression)
            return;
        session.ReconcileResponseCompressionPreference(CaptureResponseCompressionPreference());
    }

    private static void ThrowResponseCompressionPreferenceFailures(List<Exception>? failures)
    {
        if (failures is null || failures.Count == 0)
            return;
        if (failures.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException("One or more response-compression preference sessions failed to converge.", failures);
    }

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

    private RpcSession[] CaptureResponseCompressionPreferenceCohort()
    {
        var ready = _cluster is null
            ? Volatile.Read(ref _readyConnections)
            : _cluster.CaptureReadyConnections();
        if (ready.Length == 0)
            return [];

        var eligible = new List<RpcSession>(ready.Length);
        for (var index = 0; index < ready.Length; index++)
        {
            var connection = ready[index];
            if (connection.CanAcceptCalls && connection.Session.HasNegotiatedCompression)
                eligible.Add(connection.Session);
        }
        return eligible.Count == 0 ? [] : eligible.ToArray();
    }
}
