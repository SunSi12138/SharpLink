namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    // Test-only handshake used to order lifecycle races without relying on lock waiter fairness.
    private Action? _replacementStateGateEnteredForTesting = null;

    public void ReplaceInterceptors(IEnumerable<ISharpLinkClientInterceptor> interceptors)
    {
        var candidate = CreateInterceptorSnapshot(interceptors);
        lock (_stateGate)
        {
            Volatile.Read(ref _replacementStateGateEnteredForTesting)?.Invoke();
            lock (_readinessGate)
            {
                var state = State;
                if (Volatile.Read(ref _stopStarted) != 0 ||
                    state is SharpLinkConnectionState.Draining or
                        SharpLinkConnectionState.Stopped or
                        SharpLinkConnectionState.Faulted)
                {
                    throw new InvalidOperationException(
                        $"Client state '{state}' does not accept runtime interceptor replacement.");
                }

                Volatile.Write(ref _clientInterceptors, candidate);
            }
        }
    }

    private static ISharpLinkClientInterceptor[] CreateInterceptorSnapshot(
        IEnumerable<ISharpLinkClientInterceptor> interceptors)
    {
        ArgumentNullException.ThrowIfNull(interceptors);
        var candidate = interceptors.ToArray();
        for (var index = 0; index < candidate.Length; index++)
        {
            if (candidate[index] is null)
            {
                throw new ArgumentException(
                    "The interceptor sequence cannot contain null elements.",
                    nameof(interceptors));
            }
        }
        return candidate.Length == 0 ? Array.Empty<ISharpLinkClientInterceptor>() : candidate;
    }
}
