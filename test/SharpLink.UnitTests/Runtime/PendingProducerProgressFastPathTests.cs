using SharpLink.Client;
using SharpLink.Runtime;

namespace SharpLink.UnitTests.Runtime;

public class PendingProducerProgressFastPathTests
{
    [Test]
    public async Task FastPathShouldRejectAfterTerminalAndSameSlotReuse()
    {
        using var table = PendingRequestTableTestFixture.Create(capacity: 1);
        var first = table.Rent(
            Int32Codec.Instance,
            PendingCallKind.ClientStreaming,
            default,
            CancellationToken.None,
            out var firstId,
            hasResponsePayload: true,
            responseNullable: false);

        Ensure(table.TryGetProducerDeadline(firstId, out var deadline),
            "the live producer request must expose its deadline snapshot");
        Ensure(table.TryAcceptProducerProgress(firstId, deadline),
            "the live producer request must initially accept progress");

        Ensure(table.TryComplete(firstId, PendingCallCompletionReason.ConnectionClosed),
            "the first request must complete");
        await ObserveTerminalAsync(first);
        Ensure(!table.TryAcceptProducerProgress(firstId, deadline),
            "the old producer must reject immediately after terminal removal");

        for (var iteration = 0; iteration < 1_000; iteration++)
        {
            var next = table.Rent(
                Int32Codec.Instance,
                PendingCallKind.ClientStreaming,
                default,
                CancellationToken.None,
                out var nextId,
                hasResponsePayload: true,
                responseNullable: false);
            Ensure(nextId != firstId, "slot reuse must retain a distinct full request ID");
            Ensure(!table.TryAcceptProducerProgress(firstId, deadline),
                "an old request ID must never accept progress against a reused slot");
            Ensure(table.TryGetProducerDeadline(nextId, out var nextDeadline) &&
                   table.TryAcceptProducerProgress(nextId, nextDeadline),
                "the replacement request must remain independently active");
            Ensure(table.TryComplete(nextId, PendingCallCompletionReason.ConnectionClosed),
                "replacement cleanup must complete");
            await ObserveTerminalAsync(next);
        }
    }

    [Test]
    public async Task FastPathShouldClaimExpiredDeadlineBeforeTimerCallback()
    {
        var timeProvider = new ManualTimeProvider();
        using var table = PendingRequestTableTestFixture.Create(
            capacity: 1,
            timeProvider: timeProvider);
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), timeProvider);
        var operation = table.Rent(
            Int32Codec.Instance,
            PendingCallKind.ClientStreaming,
            deadline,
            CancellationToken.None,
            out var requestId,
            hasResponsePayload: true,
            responseNullable: false);

        Ensure(table.TryGetProducerDeadline(requestId, out var resolvedDeadline),
            "the live producer request must resolve its deadline");
        timeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(1));

        Ensure(!table.TryAcceptProducerProgress(requestId, resolvedDeadline),
            "deadline equality must reject producer progress before the timer callback runs");
        Ensure(!table.Contains(requestId),
            "deadline slow path must remove the request through the authoritative table terminal");
        var failure = await CaptureTerminalAsync(operation);
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "the pending operation must publish DeadlineExceeded");
    }

    [Test]
    public void FastPathShouldRejectMissingOrNonProducerRequestsAtResolution()
    {
        using var table = PendingRequestTableTestFixture.Create(capacity: 2);
        var unary = table.Rent(
            Int32Codec.Instance,
            PendingCallKind.Unary,
            default,
            CancellationToken.None,
            out var unaryId);

        Ensure(!table.TryGetProducerDeadline(unaryId, out _),
            "non-producer calls must still be rejected at the one-time resolution boundary");
        Ensure(!table.TryAcceptProducerProgress(unaryId + 1_000_000, default),
            "a missing request ID must reject lock-free progress");

        Ensure(table.TryComplete(unaryId, PendingCallCompletionReason.ConnectionClosed),
            "test cleanup must complete");
        try
        {
            _ = unary.AsValueTask().GetAwaiter().GetResult();
        }
        catch (SharpLinkException)
        {
        }
    }

    [Test]
    public async Task FastPathShouldRejectEveryTerminalOwner()
    {
        var reasons = new[]
        {
            PendingCallCompletionReason.RemoteError,
            PendingCallCompletionReason.UserCancellation,
            PendingCallCompletionReason.GoAway,
            PendingCallCompletionReason.SendFailure,
            PendingCallCompletionReason.LocalStreamComplete,
            PendingCallCompletionReason.Response
        };
        foreach (var reason in reasons)
        {
            using var table = PendingRequestTableTestFixture.Create(capacity: 1);
            var operation = table.Rent(
                Int32Codec.Instance,
                PendingCallKind.ClientStreaming,
                default,
                CancellationToken.None,
                out var requestId,
                hasResponsePayload: true,
                responseNullable: false);
            Ensure(table.TryGetProducerDeadline(requestId, out var deadline),
                "producer deadline must be resolved before terminal completion");
            Ensure(table.TryAcceptProducerProgress(requestId, deadline),
                "live producer should initially accept progress");

            Ensure(table.TryComplete(requestId, reason),
                $"terminal reason {reason} must claim the pending slot");
            Ensure(!table.TryAcceptProducerProgress(requestId, deadline),
                $"producer progress must reject a slot terminated by {reason}");
            Ensure(!table.Contains(requestId),
                "all terminal reasons must remove the authoritative slot exactly once");
            await ObserveTerminalAsync(operation);
            Ensure(table.ActiveCount == 0,
                $"terminal reason {reason} must release pending capacity");
        }
    }

    [Test]
    public async Task FastPathShouldRejectWhileTerminalOwnerCallbackIsStillRunning()
    {
        using var owner = new BlockingCompletionOwner();
        using var table = PendingRequestTableTestFixture.Create(capacity: 1, owner: owner);
        var operation = table.Rent(
            Int32Codec.Instance,
            PendingCallKind.ClientStreaming,
            default,
            CancellationToken.None,
            out var requestId,
            hasResponsePayload: true,
            responseNullable: false);
        Ensure(table.TryGetProducerDeadline(requestId, out var deadline),
            "the live producer must resolve its deadline");
        var producerToken = table.GetProducerCancellationToken(requestId);
        Ensure(!producerToken.IsCancellationRequested,
            "the producer token must start live");

        var terminal = Task.Run(() =>
            table.TryComplete(requestId, PendingCallCompletionReason.GoAway));
        try
        {
            Ensure(owner.Entered.Wait(TimeSpan.FromSeconds(5)),
                "completion must reach the owner callback after the slot was removed");
            Ensure(!table.TryAcceptProducerProgress(requestId, deadline),
                "a removed slot must reject progress even while terminal cleanup is blocked");
            Ensure(!table.Contains(requestId),
                "the authoritative slot must already be removed");
            Ensure(producerToken.IsCancellationRequested,
                "the producer cancellation token must observe the terminal");
        }
        finally
        {
            owner.AllowCompletion.Set();
        }

        Ensure(await terminal.WaitAsync(TimeSpan.FromSeconds(5)),
            "the single terminal path must finish after cleanup is released");
        await ObserveTerminalAsync(operation);
        Ensure(table.ActiveCount == 0,
            "terminal cleanup must release the pending capacity exactly once");
    }

    [Test]
    public void NoDeadlineProducerProgressMustNotAllocate()
    {
        using var table = PendingRequestTableTestFixture.Create(capacity: 1);
        var operation = table.Rent(
            Int32Codec.Instance,
            PendingCallKind.ClientStreaming,
            default,
            CancellationToken.None,
            out var requestId,
            hasResponsePayload: true,
            responseNullable: false);
        Ensure(table.TryGetProducerDeadline(requestId, out var deadline) && !deadline.HasValue,
            "this allocation control must use the actual no-deadline producer path");

        for (var i = 0; i < 10_000; i++)
            Ensure(table.TryAcceptProducerProgress(requestId, deadline),
                "warmup must keep accepting the live producer");

        var before = GC.GetAllocatedBytesForCurrentThread();
        var accepted = 0;
        for (var i = 0; i < 100_000; i++)
        {
            if (table.TryAcceptProducerProgress(requestId, deadline))
                accepted++;
        }
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Ensure(accepted == 100_000, "every no-deadline producer check must succeed");
        Ensure(bytes == 0, $"no-deadline fast-path checks allocated {bytes} bytes");

        Ensure(table.TryComplete(requestId, PendingCallCompletionReason.ConnectionClosed),
            "the allocation control must release its only slot");
        try
        {
            _ = operation.AsValueTask().GetAwaiter().GetResult();
        }
        catch (SharpLinkException)
        {
        }
    }

    private sealed class BlockingCompletionOwner : IPendingCallOwner, IDisposable
    {
        internal readonly ManualResetEventSlim Entered = new();
        internal readonly ManualResetEventSlim AllowCompletion = new();

        public void OnPendingCallRegistered() { }

        public void OnProducerCancellationCallbackFailed(Exception exception)
            => throw new InvalidOperationException(
                "The test producer has no cancellation callbacks.", exception);

        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        {
            Entered.Set();
            if (!AllowCompletion.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The terminal callback was not released.");
        }

        public void Dispose()
        {
            AllowCompletion.Set();
            Entered.Dispose();
            AllowCompletion.Dispose();
        }
    }

    private static async Task ObserveTerminalAsync(RpcRequestOperation<int> operation)
        => _ = await CaptureTerminalAsync(operation);

    private static async Task<Exception?> CaptureTerminalAsync(RpcRequestOperation<int> operation)
    {
        try
        {
            _ = await operation.AsValueTask();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
