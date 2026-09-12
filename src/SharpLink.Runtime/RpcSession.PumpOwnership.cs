namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    // Teardown must join a constructor that passed its terminal check before shutdown.
    // Otherwise disposal can miss the new pump and dispose its cancellation source first.
    private SendPump? CapturePumpForStop()
    {
        lock (_pumpGate)
            return _pump;
    }

    private SendPump GetOrCreatePump()
    {
        var pump = Volatile.Read(ref _pump);
        if (pump is not null)
            return pump;

        lock (_pumpGate)
        {
            pump = _pump;
            if (pump is not null)
                return pump;
            if (Volatile.Read(ref _terminal) is not null)
                throw GetTerminalException();

            var flushPolicyState = _compressionSendPolicyState.GetOrCreateSessionFlushPolicyState(
                _flushOptions,
                RuntimeContext.PerformanceProfile);
            pump = new SendPump(
                Output,
                flushPolicyState,
                RuntimeContext.FlowControl.MaxSendQueueBytes,
                RuntimeContext.TimeProvider,
                _lifetimeToken,
                ReturnBuffer,
                Fault);
            Volatile.Write(ref _pump, pump);
            if (Volatile.Read(ref _terminal) is { } terminal)
            {
                pump.Stop();
                throw terminal.Exception;
            }
            return pump;
        }
    }

    private SendPump GetOrCreatePumpOrReturn(IRpcByteBufferWriter packet)
    {
        try
        {
            if (Volatile.Read(ref _terminal) is { } terminal)
                throw terminal.Exception;
            return GetOrCreatePump();
        }
        catch
        {
            RuntimeContext.Buffers.Return(packet);
            throw;
        }
    }
}
