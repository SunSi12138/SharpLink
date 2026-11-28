using System.Reflection;
using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

internal static class PendingRequestTableTestFixture
{
    private const BindingFlags SlotFlags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo DenseSlots = typeof(PendingRequestTable).GetField("_slots", SlotFlags)!;
    private static readonly FieldInfo? TransitionSlots = typeof(PendingRequestTable).GetField("_transitionSlots", SlotFlags);
    private static readonly FieldInfo? InlineSlot = typeof(PendingRequestTable).GetField("_inlineSlot", SlotFlags);

    internal static IRpcCodecProvider Codecs { get; } = new Int32TestCodecProvider();

    internal static IPendingCallOwner Owner { get; } = new NoOpPendingCallOwner();

    internal static PendingRequestTable Create(
        int capacity = 65_536,
        IPendingCallOwner? owner = null,
        IRpcCodecProvider? codecs = null,
        TimeProvider? timeProvider = null)
        => new(
            capacity,
            codecs ?? Codecs,
            owner ?? Owner,
            timeProvider ?? TimeProvider.System);

    // These test checkpoints hold exactly one live object. Inspect the actual
    // pool object, not a copy, without forcing a second request to grow storage.
    internal static object CaptureSinglePendingCall(PendingRequestTable table)
    {
        var pending = InlineSlot?.GetValue(table);
        // During promotion the transition reference precedes the dense route.
        // Both fields identify the same array once the dense route is active.
        if ((DenseSlots.GetValue(table) ?? TransitionSlots?.GetValue(table)) is Array slots)
        {
            foreach (var slot in slots)
            {
                if (slot is null)
                    continue;
                if (pending is not null)
                    throw new InvalidOperationException("Expected only one live pending call at the test checkpoint.");
                pending = slot;
            }
        }

        return pending ?? throw new InvalidOperationException("Expected a live pending call at the test checkpoint.");
    }

    private sealed class Int32TestCodecProvider : IRpcCodecProvider
    {
        public IRpcCodec<T> GetCodec<T>()
        {
            if (typeof(T) == typeof(int))
                return (IRpcCodec<T>)(object)Int32Codec.Instance;
            throw new NotSupportedException($"The pending-table fixture has no codec for '{typeof(T).FullName}'.");
        }
    }

    private sealed class NoOpPendingCallOwner : IPendingCallOwner
    {
        public void OnPendingCallRegistered()
        {
        }

        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        {
        }

        public void OnProducerCancellationCallbackFailed(Exception exception)
        {
        }
    }
}
