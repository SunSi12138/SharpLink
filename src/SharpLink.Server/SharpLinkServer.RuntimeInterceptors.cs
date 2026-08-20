namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    public void ReplaceInterceptors(IEnumerable<ISharpLinkServerInterceptor> interceptors)
    {
        var candidate = CreateInterceptorSnapshot(interceptors);
        lock (_stateGate)
        {
            if (_stopTask is not null)
            {
                throw new InvalidOperationException(
                    $"Server state '{CurrentState}' does not accept runtime interceptor replacement.");
            }

            if (CurrentState is ServerState.Draining or ServerState.Stopped or ServerState.Faulted)
            {
                throw new InvalidOperationException(
                    $"Server state '{CurrentState}' does not accept runtime interceptor replacement.");
            }

            Volatile.Write(ref _serverInterceptors, candidate);
        }
    }

    private static ISharpLinkServerInterceptor[] CreateInterceptorSnapshot(
        IEnumerable<ISharpLinkServerInterceptor> interceptors)
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
        return candidate.Length == 0 ? Array.Empty<ISharpLinkServerInterceptor>() : candidate;
    }
}
