namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    private sealed partial class SendPump
    {
        private void CleanupAfterStop(List<OwnedFrame> pending, Exception terminalException)
        {
            List<Exception>? failures = null;
            try
            {
                _flushPolicyState.UnregisterChanged(_flushPolicyChanged);
            }
            catch (Exception error)
            {
                (failures ??= []).Add(error);
            }
            try
            {
                ReleaseBatch(pending, terminalException);
            }
            catch (Exception error)
            {
                (failures ??= []).Add(error);
            }
            try
            {
                DrainQueuedFrames(terminalException);
            }
            catch (Exception error)
            {
                (failures ??= []).Add(error);
            }
            try
            {
                StopWriterReadySource(terminalException);
            }
            catch (Exception error)
            {
                (failures ??= []).Add(error);
            }
            finally
            {
                PulseCapacityWaiters();
            }
            if (failures is not null)
                throw new AggregateException("Send-pump cleanup failed.", failures);
        }

        private void ReleaseBatch(List<OwnedFrame> pending, Exception? exception)
        {
            List<Exception>? failures = null;
            for (var index = 0; index < pending.Count; index++)
            {
                try
                {
                    CompleteReserved(pending[index], exception, completeFlushWaiter: true);
                }
                catch (Exception error)
                {
                    (failures ??= []).Add(error);
                }
            }
            // RunAsync's finally must never release this batch a second time.
            pending.Clear();
            if (failures is not null)
                throw new AggregateException("Send batch release failed.", failures);
        }

        private void DrainQueuedFrames(Exception exception)
        {
            List<Exception>? failures = null;
            while (_progressQueue.Reader.TryRead(out var frame))
            {
                try
                {
                    CompleteReserved(frame, exception, completeFlushWaiter: true);
                }
                catch (Exception error)
                {
                    (failures ??= []).Add(error);
                }
            }
            while (_normalQueue.Reader.TryRead(out var frame))
            {
                try
                {
                    CompleteReserved(frame, exception, completeFlushWaiter: true);
                }
                catch (Exception error)
                {
                    (failures ??= []).Add(error);
                }
            }
            if (failures is not null)
                throw new AggregateException("Send queue drain failed.", failures);
        }

        private void CompleteReserved(
            OwnedFrame frame,
            Exception? exception,
            bool completeFlushWaiter = true)
        {
            // Capture request identity before returning its pooled packet.
            var failureObserver = exception is not null && completeFlushWaiter
                ? frame.FailureObserver
                : null;
            var failedRequestId = failureObserver is null
                ? 0
                : BinaryPrimitives.ReadInt64LittleEndian(frame.Memory.Span.Slice(7, sizeof(long)));
            var readyReleaseError = exception;
            try
            {
                _returnBuffer(frame.Owner);
            }
            catch (Exception error)
            {
                readyReleaseError ??= error;
                throw;
            }
            finally
            {
                Interlocked.Add(ref _queuedBytes, -frame.Length);
                SharpLinkTelemetry.AddSendQueueBytes(-frame.Length);
                if (completeFlushWaiter)
                {
                    if (exception is null)
                        frame.FlushCompletion?.TrySetResult(true);
                    else
                        frame.FlushCompletion?.TrySetException(exception);
                }
                try
                {
                    frame.WriterReadyCompletion?.Complete(frame.WriterReadyCreditBytes, readyReleaseError);
                }
                finally
                {
                    WakeWriterReadyForCapacity();
                    PulseCapacityWaiters();
                }
                failureObserver?.OnRequestEmissionFailure(failedRequestId, exception!);
            }
        }
    }
}
