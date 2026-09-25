#if SHARPLINK_READY_WRITER_EXPERIMENT
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal sealed partial class ReadyWriterCoordinator
{
    private void CancelCapacityWaits()
    {
        List<Exception>? failures = null;
        void Record(Exception error) => (failures ??= []).Add(error);
        var cancellation = new OperationCanceledException(_stopToken);
        foreach (var stream in _streams)
        {
            lock (stream.Gate)
            {
                // A blocked transport cannot service owner commands. These frames have
                // NOT transferred to that owner, so cancel their preparation independently.
                // Never touch its ready list, current frame, B3 credit or release counters.
                DiscardPreparedLocked(stream, Record);
                try { stream.SignalSpace(cancellation); }
                catch (Exception error) { Record(error); }
            }
        }
        // Do not report durable writer completion from the cancellation callback.
        // Stopped joins this registration and then publishes any cleanup failures.
        if (failures is not null)
            Volatile.Write(ref _preparationCleanupFailure,
                new AggregateException("Canceled preparation cleanup failed.", failures));
    }

    private void DiscardPreparedLocked(Stream stream, Action<Exception> record)
    {
        while (stream.Frames.TryDequeue(out var packet))
        {
            stream.QueuedBytes -= packet.WrittenCount;
            try
            {
                _context.Buffers.Return(packet);
                // Pump stop may drain a different stream concurrently with cancellation.
                // This is a cold cleanup counter, not a new per-item hot-path RMW.
                Interlocked.Increment(ref _discarded);
            }
            catch (Exception error) { record(error); }
            if (_reference is not null)
            {
                try { _reference.ReturnUnsentCredit(in stream.Lease, _bytes); }
                catch (Exception error) { record(error); }
            }
        }
    }

    public void Stopped(Exception error)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        List<Exception>? failures = null;
        void Record(Exception failure) => (failures ??= [error]).Add(failure);

        _notifications.Writer.TryComplete(error);
        _updates.Writer.TryComplete(error);
        _pendingAdmission?.Fail(error);
        _pendingAdmission = null;
        while (_updates.Reader.TryRead(out var pending)) pending.Operation?.Fail(error);
        try { _cancel.Cancel(); }
        catch (Exception failure) { Record(failure); }
        foreach (var stream in _streams)
        {
            lock (stream.Gate)
            {
                // Idempotent with early cancellation. Writer-owned frames are no longer
                // in this ring and remain governed by the existing pump settlement.
                DiscardPreparedLocked(stream, Record);
                try { stream.SignalSpace(error); }
                catch (Exception failure) { Record(failure); }
            }
        }
        // Join a concurrent callback outside all stream gates before reading its errors.
        try { _capacityCancellation.Dispose(); }
        catch (Exception failure) { Record(failure); }
        var preparationFailure = Volatile.Read(ref _preparationCleanupFailure);
        if (preparationFailure is not null) Record(preparationFailure);
        _settled.TrySetException(failures is null
            ? error : new AggregateException("Ready writer stop and cleanup failed.", failures));
    }
}
#endif
