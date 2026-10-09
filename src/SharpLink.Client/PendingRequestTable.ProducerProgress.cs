using System.Runtime.CompilerServices;
using SharpLink.Abstractions;

namespace SharpLink.Client;

internal sealed partial class PendingRequestTable
{
    /// <summary>
    /// Issue #737 candidate A1: validate producer progress from the authoritative slot identity
    /// without reacquiring the PendingCall completion gate on every stream item. The deadline was
    /// resolved from this same lifecycle before the producer loop starts; expiration falls back to
    /// the table's existing single terminal authority.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryAcceptProducerProgress(long id, RpcDeadline deadline)
    {
        if (!Contains(id))
            return false;
        if (!deadline.IsExpired(_timeProvider))
            return true;

        TryComplete(id, PendingCallCompletionReason.DeadlineExceeded);
        return false;
    }
}
