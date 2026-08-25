using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

public class PendingRequestTableWrapTests
{
    [Test]
    public async Task DeadlineSchedulerShouldPreserveOrderAcrossSignedTimestampBoundary()
    {
        var start = long.MaxValue - TimeSpan.FromMilliseconds(500).Ticks;
        var timeProvider = new WrappingManualTimeProvider(start);
        using var manager = new PendingRequestTable(
            8,
            new Int32OnlyCodecProvider(),
            new NoopPendingCallOwner(),
            timeProvider);

        var firstDeadline = RpcDeadline.Create(TimeSpan.FromMilliseconds(250), timeProvider);
        var wrappedLaterDeadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), timeProvider);
        Ensure(firstDeadline.Timestamp > 0 && wrappedLaterDeadline.Timestamp < 0,
            "the fixture must place the later deadline across the signed timestamp boundary");

        var first = manager.Rent(
            Int32Codec.Instance,
            PendingCallKind.Unary,
            firstDeadline,
            CancellationToken.None,
            out _).AsValueTask().AsTask();
        var later = manager.Rent(
            Int32Codec.Instance,
            PendingCallKind.Unary,
            wrappedLaterDeadline,
            CancellationToken.None,
            out _).AsValueTask().AsTask();

        timeProvider.Advance(TimeSpan.FromMilliseconds(250));
        Ensure(await CaptureFailureAsync(first) is SharpLinkException
            { Code: SharpLinkErrorCode.DeadlineExceeded },
            "the pre-wrap deadline must expire first even though the later raw target is signed-negative");
        Ensure(!later.IsCompleted && manager.Count == 1,
            "the wrapped later deadline must remain live after the earlier deadline fires");

        timeProvider.Advance(TimeSpan.FromMilliseconds(750));
        Ensure(await CaptureFailureAsync(later) is SharpLinkException
            { Code: SharpLinkErrorCode.DeadlineExceeded },
            "the wrapped later deadline must expire at its own modular boundary");
        Ensure(manager.Count == 0,
            "cross-boundary scheduler scans must release both pending slots in deadline order");
    }

    private static async Task<Exception> CaptureFailureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new Exception("expected failure");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class Int32OnlyCodecProvider : IRpcCodecProvider
    {
        public IRpcCodec<T> GetCodec<T>()
        {
            if (typeof(T) == typeof(int))
                return (IRpcCodec<T>)(object)Int32Codec.Instance;
            throw new NotSupportedException(typeof(T).FullName);
        }
    }

    private sealed class NoopPendingCallOwner : IPendingCallOwner
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
