using System.Runtime.CompilerServices;
using SharpLink.Server;
using static SharpLink.UnitTests.Server.ServerCallbackOwnershipFixture;

namespace SharpLink.UnitTests.Server;

public class ServerCancellationCallbackOwnershipTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task RemovedInvocationMustNotHidePhysicalCallback(bool timer)
        => LongRunningTestWorker.Run(() =>
        {
            using var fixture = new ServerCallbackOwnershipFixture();
            fixture.Trigger(timer);
            fixture.ReleaseInvocation();
            var drain = fixture.Connection.DrainPhysicalCancellationCallbacksAsync();
            Ensure(!drain.IsCompleted, "empty maps and zero calls do not mean callback completion");
            fixture.CallbackRelease.Set();
            LongRunningTestWorker.WaitForPhase(drain, Bound);
            Ensure(fixture.Payload.DisposeCount == 1, "physical ownership must release exactly once");
        });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task DrainMustIncludeReturnCoreAndLateCleanupFailure(bool timer)
        => LongRunningTestWorker.Run(() =>
        {
            using var fixture = new ServerCallbackOwnershipFixture();
            fixture.Payload.Release.Reset();
            fixture.Payload.ThrowOnDispose = true;
            fixture.Trigger(timer);
            fixture.ReleaseInvocation();
            var drain = fixture.Connection.DrainPhysicalCancellationCallbacksAsync();
            Ensure(!drain.IsCompleted, "begin drain before the callback can publish a cleanup failure");
            fixture.CallbackRelease.Set();
            LongRunningTestWorker.WaitForPhase(fixture.Payload.Entered.Task, Bound);
            Ensure(!drain.IsCompleted, "ReturnCore remains owned after the token callback returns");
            fixture.Payload.Release.Set();
            ExpectCleanupFailure(drain);
            // In the timer case, no sidecar existed when drain began. Its newly published failure
            // must be seen after _scanRunning becomes zero, and by every later certificate.
            ExpectCleanupFailure(fixture.Connection.DrainPhysicalCancellationCallbacksAsync());
        });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task OrdinaryCallbackExceptionMustNotPoisonPhysicalDrain(bool timer)
        => LongRunningTestWorker.Run(() =>
        {
            using var fixture = new ServerCallbackOwnershipFixture(callback: static () =>
                throw new InvalidOperationException("ordinary best-effort callback failure"));
            fixture.Trigger(timer);
            fixture.ReleaseInvocation();
            var drain = fixture.Connection.DrainPhysicalCancellationCallbacksAsync();
            fixture.CallbackRelease.Set();
            LongRunningTestWorker.WaitForPhase(drain, Bound);
            LongRunningTestWorker.WaitForPhase(fixture.Connection.DrainPhysicalCancellationCallbacksAsync(), Bound);
        });

    [Test]
    public Task OneConnectionMustNotWaitForAnUnrelatedCallback()
        => LongRunningTestWorker.Run(() =>
        {
            using var held = new ServerCallbackOwnershipFixture();
            using var unrelated = new ServerCallbackOwnershipFixture();
            held.Trigger(timer: false);
            held.ReleaseInvocation();
            unrelated.ReleaseInvocation();
            LongRunningTestWorker.WaitForPhase(unrelated.Connection.DrainPhysicalCancellationCallbacksAsync(), Bound);
            Ensure(!held.Connection.DrainPhysicalCancellationCallbacksAsync().IsCompleted,
                "map-scoped ownership must neither cross-wait nor lose the held callback");
        });

    [Test]
    public Task DetachedCallbackMustObserveItsOwnerBeforeItCanRun()
        => LongRunningTestWorker.Run(() =>
        {
            using var fixture = new ServerCallbackOwnershipFixture();
            var published = false;
            fixture.AtCallbackEntry = () => published =
                !ServerCancellationCallbackWork.CaptureDrain(fixture.Connection.CallCancellations).IsCompleted;
            fixture.Trigger(timer: false);
            Ensure(published, "the callback must never start before its cold owner is published");
        });

    [Test]
    public Task CompletedBatchMustPublishLateFailureWithoutRetainingOriginalPayload()
        => LongRunningTestWorker.Run(() =>
        {
            var retained = CreateCompletedBatchAndLateFailure();
            ExpectCleanupFailure(retained.Drain);
            for (var attempt = 0; attempt < 8 && retained.Payload.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            Ensure(!retained.Payload.IsAlive, "sticky lifecycle diagnostics must not retain the original exception graph");
            ExpectCleanupFailure(ServerCancellationCallbackWork.CaptureDrain(retained.Calls));
            GC.KeepAlive(retained);
        });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (StripedLongMap<ServerCallCancellationState> Calls, Task Drain, WeakReference Payload)
        CreateCompletedBatchAndLateFailure()
    {
        using var fixture = new ServerCallbackOwnershipFixture();
        fixture.Trigger(timer: false);
        fixture.ReleaseInvocation();
        fixture.CallbackRelease.Set();
        LongRunningTestWorker.WaitForPhase(fixture.Connection.DrainPhysicalCancellationCallbacksAsync(), Bound);
        var calls = fixture.Connection.CallCancellations;
        var completed = ServerCancellationCallbackWork.CaptureDrain(calls);
        Ensure(completed.IsCompletedSuccessfully, "establish the successful previous batch");
        var payload = new object();
        var weak = new WeakReference(payload);
        var failure = new InvalidOperationException("late physical resource cleanup failure");
        failure.Data.Add("payload", payload);
        ServerCancellationCallbackWork.RecordCleanupFailure(calls, failure);
        var drain = ServerCancellationCallbackWork.CaptureDrain(calls);
        Ensure(!ReferenceEquals(completed, drain), "a completed successful batch cannot hide a new lifecycle fault");
        return (calls, drain, weak);
    }

    private static void ExpectCleanupFailure(Task drain)
    {
        try
        {
            LongRunningTestWorker.WaitForPhase(drain, Bound);
            throw new Exception("failed physical cleanup must not certify a drain");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("Physical cancellation work", StringComparison.Ordinal))
        {
            Ensure(exception.InnerException is null, "the sticky diagnostic must not retain the original user graph");
        }
    }
}
