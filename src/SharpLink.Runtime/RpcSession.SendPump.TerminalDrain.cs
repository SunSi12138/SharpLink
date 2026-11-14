namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    private sealed partial class SendPump
    {
        private void DrainTerminalFrames(List<OwnedFrame> pending, Exception terminalException)
        {
            List<Exception>? failures = null;
            for (var index = 0; index < pending.Count; index++)
            {
                var frame = pending[index];
                pending[index] = default;
                CompleteTerminalFrame(frame, terminalException, ref failures);
            }
            pending.Clear();
            try { DrainQueuedFrames(terminalException); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
            PulseCapacityWaiters();
            ThrowTerminalFrameFailures(failures);
        }

        private void DrainQueuedFrames(Exception terminalException)
        {
            List<Exception>? failures = null;
            while (_progressQueue.Reader.TryRead(out var frame))
                CompleteTerminalFrame(frame, terminalException, ref failures);
            while (_normalQueue.Reader.TryRead(out var frame))
                CompleteTerminalFrame(frame, terminalException, ref failures);
            ThrowTerminalFrameFailures(failures);
        }

        private static void ThrowTerminalFrameFailures(List<Exception>? failures)
        {
            // A pending-call observer is part of this joined pump lifetime. Its failure must
            // remain visible, but must not retain the next frame, either queue or flush marker.
            if (failures is { Count: 1 })
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures is not null)
                throw new AggregateException(failures);
        }

        private void CompleteTerminalFrame(OwnedFrame frame, Exception terminalException, ref List<Exception>? failures)
        {
            try
            {
                CompleteReserved(frame, terminalException, completeFlushWaiter: true);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }
    }
}
