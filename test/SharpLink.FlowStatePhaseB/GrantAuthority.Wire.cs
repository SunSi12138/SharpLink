namespace SharpLink.FlowStatePhaseB;

internal sealed partial class GrantAuthority
{
    private readonly Lock _wireSubmissionGate = new();
    private WireUpdateCommand? _wireCommand;

    // Wire carries a key, not our internal generation. Resolve only on the same owner
    // that orders Open/Close. This is a conservative accounting CONTROL: excess stream
    // credit is reported, not silently accepted as another stream's connection credit.
    // The frozen runtime independently clamps stream and connection windows; this API
    // must not be installed as a compatible replacement until that difference is resolved.
    internal readonly record struct WireObservation(bool Matched, int Returned, int Excess);

    internal ValueTask<WireObservation> ObserveWindowUpdateAsync(long requestId, ushort streamId, int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        lock (_wireSubmissionGate)
        {
            var command = _wireCommand;
            if (command is null || command.IsBusy)
                _wireCommand = command = new WireUpdateCommand(this);
            return SubmitReusable(command, command.Prepare(new StreamKey(requestId, streamId), bytes));
        }
    }

    private WireObservation ObserveWindowUpdate(StreamKey key, int bytes)
    {
        if (_terminal) throw new InvalidOperationException("Connection is terminal.");
        if (!_states.TryGetValue(key, out var state))
            return new WireObservation(false, 0, bytes);
        lock (state.Gate)
        {
            var returned = checked((int)Math.Min(state.Outstanding, bytes));
            // Same ordered, debt-bounded operation as generation-tagged model events.
            // In particular it must not credit a local unspent grant or unsent receipt.
            ApplyWindowUpdate(new Lease(state, state.Generation), bytes);
            return new WireObservation(true, returned, bytes - returned);
        }
    }

    private sealed class WireUpdateCommand : ReusableOwnerCommand<WireObservation>
    {
        private readonly GrantAuthority _owner;
        private StreamKey _key;
        private int _bytes;

        internal WireUpdateCommand(GrantAuthority owner)
        {
            _owner = owner;
            Interlocked.Increment(ref owner.ReusableCommandAllocations);
        }

        internal ValueTask<WireObservation> Prepare(StreamKey key, int bytes)
        {
            var result = Begin();
            _key = key;
            _bytes = bytes;
            return result;
        }

        public override void Execute()
        {
            WireObservation result;
            try { result = _owner.ObserveWindowUpdate(_key, _bytes); }
            catch (Exception error) { Fail(error); return; }
            Complete(result);
        }
    }
}
