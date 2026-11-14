#if SHARPLINK_775_STATIC_SLICE
namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    internal void FlushOneWayConsumedCredit(long requestId, int count)
    {
        var controller = Volatile.Read(ref _protocolState).FlowController;
        if (controller is null) return;
        for (var index = 1; index <= count; index++)
        {
            var streamId = checked((ushort)index);
            var credit = controller.FlushActiveConsumed(requestId, streamId);
            if (credit != 0) TrySendWindowUpdate(requestId, streamId, credit);
        }
        DrainConsumedCreditUpdates(controller);
    }
}

internal sealed partial class StreamFlowController
{
    // Consumer abandonment returns already-consumed bytes without pretending the producer
    // reached peer terminal. Keep the receive state and stable discard route fully active.
    internal int FlushActiveConsumed(long requestId, ushort streamId)
    {
        lock (_gate)
        {
            if (_terminalException is not null || !_receiveStates.TryGetValue(new StreamKey(requestId, streamId), out var state)) return 0;
            return TakePendingCredit(state);
        }
    }
}
#endif
