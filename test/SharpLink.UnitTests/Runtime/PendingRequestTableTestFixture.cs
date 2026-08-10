using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

internal static class PendingRequestTableTestFixture
{
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
