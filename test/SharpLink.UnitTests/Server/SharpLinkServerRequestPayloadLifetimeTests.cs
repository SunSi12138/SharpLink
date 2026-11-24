using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using static SharpLink.UnitTests.Server.SharpLinkServerRequestScopeReviewTests;

namespace SharpLink.UnitTests.Server;

public class SharpLinkServerRequestPayloadLifetimeTests
{
    private const long RequestId = 407;

    [Test]
    public async Task PendingAsyncUnaryDoesNotRetainRawRequestBacking()
    {
        using var loggerFactory = new ScopeCaptureLoggerFactory();
        var stub = new ControlledStub(StubBehavior.LogAfterSignal,
            loggerFactory.CreateLogger("PayloadLifetimeService"));
        await using var harness = new DispatchHarness(loggerFactory, stub);

        try
        {
            var (dispatch, backing) = StartPendingDispatch(harness);

            await Assert.That(dispatch.IsCompleted).IsFalse();
            await AssertCollectedAsync(
                backing,
                "pending async request dispatch retained the raw PipeReader payload backing");

            stub.Signal();
            await dispatch;
        }
        finally
        {
            stub.Signal();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task Dispatch, WeakReference<byte[]> Backing) StartPendingDispatch(DispatchHarness harness)
    {
        var payload = new byte[1024 * 1024];
        var backing = new WeakReference<byte[]>(payload);
        var dispatch = harness.DispatchPreparedPayload(RequestId, payload);
        return (dispatch, backing);
    }

    private static async Task AssertCollectedAsync(WeakReference<byte[]> backing, string failureMessage)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            if (!IsAlive(backing))
                return;
            await Task.Yield();
        }

        throw new Exception(failureMessage);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<byte[]> backing)
        => backing.TryGetTarget(out _);

}
