#if SHARPLINK_READY_WRITER_EXPERIMENT
namespace SharpLink.Benchmarks;

internal sealed partial class ReadyWriterCoordinator
{
    private long _wireCreditBytesObserved, _excessWireCreditBytes, _ignoredWireUpdates;

    // Owner-only wire boundary. Permission and settled DATA are different ledgers:
    // a duplicate may restore connection permission without settling another stream.
    private void ApplyWireUpdate(Update update)
    {
        _wireCreditBytesObserved = checked(_wireCreditBytesObserved + update.Bytes);
        if (update.StreamId != 1 || update.RequestId < 1 || update.RequestId > _streams.Length)
        {
            _ignoredWireUpdates++;
            return;
        }
        var target = _streams[checked((int)update.RequestId - 1)];
        if (_reference is null && target.Taken == 0)
        {
            // Fixed slots are not send-state admission. Like the original controller,
            // a key with no first admission cannot manufacture connection permission.
            _ignoredWireUpdates++;
            return;
        }
        var returned = Math.Min(target.Outstanding, update.Bytes);
        if (_reference is not null)
            _reference.ApplyWindowUpdate(update.RequestId, update.StreamId, update.Bytes);
        else
        {
            // Match the frozen controller's independent permission clamps. There are
            // no speculative local credit grants in the B3 writer-owned path.
            var streamCredit = Math.Min(checked(target.Credit + update.Bytes), _window);
            var connectionCredit = Math.Min(checked(_connectionCredit + update.Bytes), _connectionWindow);
            target.Credit = streamCredit;
            _connectionCredit = connectionCredit;
        }
        // Account only this stream's outstanding DATA, in both comparison modes.
        // Do not let excess/unknown update bytes complete the joined transport task.
        target.Outstanding -= returned;
        _returned = checked(_returned + returned);
        _excessWireCreditBytes = checked(_excessWireCreditBytes + update.Bytes - returned);
        _blocked = false;
    }

    private void ReturnWriterUnsent(Stream stream, int creditBytes)
    {
        if (_reference is not null)
            _reference.ReturnUnsentCredit(in stream.Lease, creditBytes);
        else
        {
            var streamCredit = checked(stream.Credit + creditBytes);
            var connectionCredit = checked(_connectionCredit + creditBytes);
            // The existing API rejects, rather than clamps, an over-return. Validate
            // both windows before any mutation, including after an excess wire update.
            if (streamCredit > _window || connectionCredit > _connectionWindow)
                throw new InvalidOperationException("Unsent stream credit was returned more than once.");
            stream.Credit = streamCredit;
            _connectionCredit = connectionCredit;
        }
        stream.Outstanding -= creditBytes;
    }
}
#endif
