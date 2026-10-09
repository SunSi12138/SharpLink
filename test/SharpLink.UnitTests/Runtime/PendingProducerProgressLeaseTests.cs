using SharpLink.Client;
using SharpLink.Runtime;

namespace SharpLink.UnitTests.Runtime;

public class PendingProducerProgressLeaseTests
{
    [Test]
    public async Task ResolvedLeaseShouldRejectEveryLaterLifecycleInTheSameSlot()
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

        Ensure(table.TryResolveProducerProgress(firstId, out var stale, out _),
            "the first client-stream request must resolve a producer-progress lease");
        Ensure(stale.IsActive(), "the resolved lease must initially observe its own slot");

        Ensure(table.TryComplete(firstId, PendingCallCompletionReason.ConnectionClosed),
            "the first request must complete");
        await ObserveTerminalAsync(first);
        Ensure(!stale.IsActive(), "terminal removal must invalidate the old lease immediately");

        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            var next = table.Rent(
                Int32Codec.Instance,
                PendingCallKind.ClientStreaming,
                default,
                CancellationToken.None,
                out var nextId,
                hasResponsePayload: true,
                responseNullable: false);
            Ensure(nextId != firstId, "a reused slot must retain a distinct request identity");
            Ensure(!stale.IsActive(),
                "an old lease must never become active again when the same physical slot is reused");
            Ensure(table.TryResolveProducerProgress(nextId, out var current, out _),
                "the replacement lifecycle must resolve normally");
            Ensure(current.IsActive(), "the replacement lease must observe its own lifecycle");
            Ensure(table.TryComplete(nextId, PendingCallCompletionReason.ConnectionClosed),
                "the replacement lifecycle must complete");
            await ObserveTerminalAsync(next);
            Ensure(!current.IsActive(), "each completed replacement lease must become stale");
        }
    }

    [Test]
    public async Task DeadlineSnapshotShouldRemainAuthoritativeWithoutPerItemPendingCallAccess()
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

        Ensure(table.TryResolveProducerProgress(
                requestId,
                out var lease,
                out var resolvedDeadline),
            "the live request must resolve");
        Ensure(lease.IsActive(), "the live request must be active");
        Ensure(!resolvedDeadline.IsExpired(timeProvider),
            "the captured deadline must initially be live");

        timeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(1));
        Ensure(lease.IsActive(),
            "crossing the monotonic deadline must not fabricate a second terminal authority");
        Ensure(resolvedDeadline.IsExpired(timeProvider),
            "the captured deadline must detect equality even before the timer callback runs");
        Ensure(table.TryComplete(
                requestId,
                PendingCallCompletionReason.DeadlineExceeded),
            "the table must remain the authoritative deadline terminal winner");
        Ensure(!lease.IsActive(),
            "the authoritative terminal removal must invalidate the resolved slot lease");
        await ObserveTerminalAsync(operation);
    }

    [Test]
    public void ResolveShouldRejectNonProducerAndMissingCalls()
    {
        using var table = PendingRequestTableTestFixture.Create(capacity: 2);
        var unary = table.Rent(
            Int32Codec.Instance,
            PendingCallKind.Unary,
            default,
            CancellationToken.None,
            out var unaryId);

        Ensure(!table.TryResolveProducerProgress(unaryId, out _, out _),
            "a unary pending call must not resolve a producer lease");
        Ensure(!table.TryResolveProducerProgress(unaryId + 1_000_000, out _, out _),
            "a missing request must not resolve a producer lease");

        Ensure(table.TryComplete(unaryId, PendingCallCompletionReason.ConnectionClosed),
            "test unary cleanup must complete");
        try
        {
            _ = unary.AsValueTask().GetAwaiter().GetResult();
        }
        catch (SharpLinkException)
        {
        }
    }

    private static async Task ObserveTerminalAsync(RpcRequestOperation<int> operation)
    {
        try
        {
            _ = await operation.AsValueTask();
        }
        catch (SharpLinkException)
        {
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
