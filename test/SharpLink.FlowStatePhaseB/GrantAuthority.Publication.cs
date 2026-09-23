using System.Threading.Channels;

namespace SharpLink.FlowStatePhaseB;

internal sealed partial class GrantAuthority
{
    // Separate the admitted receipt from the writer's ownership of those bytes.
    // This is still a model contract: it does not attach to a production SendPump.
    internal readonly record struct Publication(Receipt Receipt);

    internal Publication BeginPublication(Receipt receipt)
    {
        var state = receipt.Lease.State ?? throw new InvalidOperationException("Unresolved receipt.");
        lock (state.Gate)
        {
            Validate(receipt.Lease);
            if (state.Closed || state.PublicationActive || state.Pending != receipt.Bytes ||
                state.Sequence != receipt.Sequence || receipt.Bytes <= 0)
                throw new InvalidOperationException("Receipt already settled or revoked.");
            state.PublicationActive = true;
            state.PublicationUncredited = state.PublicationBytes = state.Pending;
            state.Outstanding += state.Pending;
            state.Pending = 0;
            return new Publication(receipt);
        }
    }

    // accepted=false requires the writer to know that no byte became peer-visible.
    // An ambiguous transport failure must fault the connection, not return credit.
    internal ValueTask FinishPublicationAsync(Publication publication, bool accepted)
    {
        var receipt = publication.Receipt;
        var state = receipt.Lease.State ?? throw new InvalidOperationException("Unresolved publication.");
        lock (state.Gate)
        {
            Validate(receipt.Lease);
            if (!state.PublicationActive || state.Sequence != receipt.Sequence ||
                state.PublicationBytes != receipt.Bytes || receipt.Bytes <= 0)
                throw new InvalidOperationException("Publication already settled or belongs to another receipt.");
            if (!accepted)
            {
                if (state.PublicationUncredited != receipt.Bytes)
                    throw new InvalidOperationException("A peer-observed publication cannot be returned as unsent.");
                state.Outstanding -= receipt.Bytes;
                state.Credit += receipt.Bytes;
                state.Grant += receipt.Bytes;
            }
            state.PublicationActive = false;
            state.PublicationUncredited = state.PublicationBytes = 0;
            // Normal successful publication has no connection-owner command. Closed
            // states and pressure refunds reconcile with the authority on the cold path.
            if (accepted
                ? !state.Closed || state.Outstanding != 0
                : !state.Closed && Volatile.Read(ref _pressure) == 0)
                return ValueTask.CompletedTask;
        }
        return ReconcilePublicationAsync(receipt.Lease);
    }

    private async ValueTask ReconcilePublicationAsync(Lease lease)
    {
        // A terminal connection has no further admission or pool reuse. Do not submit
        // cleanup to its closed queue; the last writer merely releases its local pin.
        if (Volatile.Read(ref _terminal)) return;
        try
        {
            await OnOwnerAsync(() =>
            {
                lock (lease.State.Gate)
                {
                    var state = lease.State;
                    if (!state.Attached || state.Generation != lease.Generation) return false;
                    _free += state.Grant;
                    state.Grant = 0;
                    TryRetireClosed(state);
                    return true;
                }
            }).ConfigureAwait(false);
        }
        catch (ChannelClosedException) when (Volatile.Read(ref _terminal))
        {
            // Disposal won the enqueue race. No living connection can reuse this credit.
        }
    }

    // Called only by the connection owner while holding the stream gate. A closed
    // state cannot be recycled merely because an early WindowUpdate restored credit.
    private void TryRetireClosed(State state)
    {
        if (!state.Closed || state.Pending != 0 || state.Outstanding != 0 || state.PublicationActive) return;
        _free += state.Grant;
        state.Grant = 0;
        Retire(state);
    }
}
