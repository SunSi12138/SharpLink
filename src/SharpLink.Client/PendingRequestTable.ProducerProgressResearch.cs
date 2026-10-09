using System.Runtime.CompilerServices;
using SharpLink.Abstractions;

namespace SharpLink.Client;

internal sealed partial class PendingRequestTable
{
    /// <summary>
    /// Research-only #737 resolved producer-progress handle. The lease retains the stable slot
    /// array/index and the full request ID, but never a pooled PendingCall reference. Reuse of the
    /// same physical PendingCall object for another request therefore remains rejected by ID.
    /// </summary>
    internal bool TryResolveProducerProgress(
        long id,
        out ProducerProgressLease lease,
        out RpcDeadline deadline)
    {
        var slots = Volatile.Read(ref _slots);
        if (slots is null)
        {
            lease = default;
            deadline = default;
            return false;
        }

        var index = (int)(id & _indexMask);
        var current = Volatile.Read(ref slots[index]);
        if (current is null || current.Id != id ||
            current.Kind is not (PendingCallKind.OneWayClientStreaming or
                                 PendingCallKind.ClientStreaming or
                                 PendingCallKind.DuplexStreaming))
        {
            lease = default;
            deadline = default;
            return false;
        }

        lock (current.CompletionGate)
        {
            if (!ReferenceEquals(Volatile.Read(ref slots[index]), current) || current.Id != id ||
                current.Kind is not (PendingCallKind.OneWayClientStreaming or
                                     PendingCallKind.ClientStreaming or
                                     PendingCallKind.DuplexStreaming))
            {
                lease = default;
                deadline = default;
                return false;
            }

            deadline = current.Deadline;
            lease = ProducerProgressLease.Create(this, index, id);
            return true;
        }
    }

    internal readonly struct ProducerProgressLease
    {
        private readonly PendingCall?[]? _slots;
        private readonly int _index;
        private readonly long _id;

        private ProducerProgressLease(PendingCall?[] slots, int index, long id)
        {
            _slots = slots;
            _index = index;
            _id = id;
        }

        internal static ProducerProgressLease Create(
            PendingRequestTable owner,
            int index,
            long id)
        {
            var slots = Volatile.Read(ref owner._slots)
                ?? throw new InvalidOperationException("Producer-progress slots are unavailable.");
            return new ProducerProgressLease(slots, index, id);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool IsActive()
        {
            var slots = _slots;
            if (slots is null)
                return false;

            var current = Volatile.Read(ref slots[_index]);
            return current is not null && current.Id == _id;
        }
    }
}
