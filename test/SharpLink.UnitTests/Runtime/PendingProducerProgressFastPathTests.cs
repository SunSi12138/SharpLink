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
