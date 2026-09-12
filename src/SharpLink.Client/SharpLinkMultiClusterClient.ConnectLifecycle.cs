using System.Runtime.ExceptionServices;

namespace SharpLink.Client;

internal sealed partial class SharpLinkMultiClusterClient
{
    private async Task RethrowInitialConnectFailureAsync(
        Exception connectException,
        SharpLinkClusterSlot[] capturedSlots)
    {
        var lifecycle = (SharpLinkClientLifecycleState)Volatile.Read(ref _lifecycleState);
        if (lifecycle is SharpLinkClientLifecycleState.Starting or SharpLinkClientLifecycleState.Running)
        {
            _ = Interlocked.CompareExchange(
                ref _state,
                (int)SharpLinkMultiClusterState.Degraded,
                (int)SharpLinkMultiClusterState.Connecting);
            ExceptionDispatchInfo.Capture(connectException).Throw();
        }

        var failures = new List<Exception> { connectException };
        await StopSlotsAsync(capturedSlots, failures).ConfigureAwait(false);
        // StopAsync owns the terminal transition. A connect completion may only replace the
        // original Connecting state, never Draining or Stopped.
        _ = Interlocked.CompareExchange(
            ref _state,
            (int)SharpLinkMultiClusterState.Faulted,
            (int)SharpLinkMultiClusterState.Connecting);
        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(connectException).Throw();
        throw new AggregateException(failures);
    }
}
