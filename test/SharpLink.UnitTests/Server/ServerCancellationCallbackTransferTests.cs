using SharpLink.Server;
using static SharpLink.UnitTests.Server.ServerCallbackOwnershipFixture;

namespace SharpLink.UnitTests.Server;

public class ServerCancellationCallbackTransferTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task RegistryLookupFailureStillOwnsSynchronousLeaseCleanup(bool cleanupFails)
        => LongRunningTestWorker.Run(() =>
        {
            using var fixture = new ServerCallbackOwnershipFixture();
            Ensure(fixture.Connection.CallCancellations.TryCapture(41,
                static (id, state) => state.CaptureLease(id), out var lease) && lease.TryAcquire(),
                "transfer an acquired external-use lease, not the invocation owner");
            fixture.Payload.Release.Reset();
            fixture.Payload.ThrowOnDispose = cleanupFails;
            fixture.ReleaseInvocation();
            Ensure(fixture.Payload.DisposeCount == 0, "the transferred lease alone retains physical cleanup");

            // An invalid registry key deterministically fails before scheduling without adding
            // a production hook or exhausting memory. This is not an injected Task.Run failure.
            var work = LongRunningTestWorker.Run(() => ServerCancellationCallbackWork.QueueNotification(null!, lease));
            try
            {
                LongRunningTestWorker.WaitForPhase(fixture.Payload.Entered.Task, Bound);
                Ensure(!work.IsCompleted, "the original failure cannot escape before local ReleaseUse finishes");
                Ensure(fixture.Payload.DisposeCount == 1, "the cold helper releases its lease exactly once");
                fixture.Payload.Release.Set();
                var failure = CaptureFailure(work);
                if (cleanupFails)
                {
                    Ensure(failure is AggregateException { InnerExceptions.Count: 3 },
                        "preserve lookup, physical cleanup and invalid-key failure-publication errors");
                    var failures = ((AggregateException)failure).InnerExceptions;
                    Ensure(failures[0] is ArgumentNullException { ParamName: "key" } &&
                        failures[1] is InvalidOperationException { Message: "physical payload cleanup failed" } &&
                        failures[2] is ArgumentNullException { ParamName: "key" },
                        "cleanup or diagnostic failure must not replace the original lookup error");
                }
                else
                {
                    Ensure(failure is ArgumentNullException { ParamName: "key", InnerException: null },
                        "successful cleanup rethrows the original lookup error without wrapping it");
                }
                Ensure(fixture.Payload.DisposeCount == 1, "neither caller nor worker may release the failed transfer twice");
            }
            finally
            {
                fixture.Payload.Release.Set();
                LongRunningTestWorker.Join(work, Bound);
            }
        });

    [Test]
    public Task CallbackCanReentrantlyReleaseItsParentWithoutLosingPublishedOwnership()
        => LongRunningTestWorker.Run(() =>
        {
            using var fixture = new ServerCallbackOwnershipFixture();
            Task? drain = null;
            fixture.AtCallbackEntry = () =>
            {
                fixture.ReleaseInvocation();
                drain = fixture.Connection.DrainPhysicalCancellationCallbacksAsync();
            };
            fixture.Trigger(timer: false);
            Ensure(drain is { IsCompleted: false } && fixture.Payload.DisposeCount == 0,
                "reentrant parent release still leaves the published callback and payload owned");
            fixture.CallbackRelease.Set();
            LongRunningTestWorker.WaitForPhase(drain!, Bound);
            Ensure(fixture.Payload.DisposeCount == 1, "the worker eventually releases its lease exactly once");
        });

    private static Exception CaptureFailure(Task work)
    {
        try
        {
            LongRunningTestWorker.WaitForPhase(work, Bound);
        }
        catch (Exception exception)
        {
            return exception;
        }
        throw new Exception("the invalid registry key must fail before scheduling");
    }
}
