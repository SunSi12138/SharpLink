using SharpLink.Server;
using SharpLink.UnitTests.Runtime;
using static SharpLink.UnitTests.Server.ServerCallbackOwnershipFixture;

namespace SharpLink.UnitTests.Server;

public class ServerCancellationQueuedShutdownTests
{
    [Test]
    public Task ClosedQueuedAdmissionMustNotFaultConnectionCleanupWhileItsContinuationIsPending()
        => LongRunningTestWorker.Run(() =>
        {
            var clock = new ManualTimeProvider();
            var connection = CreateConnection(clock);
            Ensure(connection.MarkReady(null), "publish the ordinary queued connection");
            var service = new TrackingService();
            var registration = CreateService(service);
            _ = LongRunningTestWorker.WaitForPhase(connection.AcquireServiceAsync(registration, default).AsTask(), Bound);
            var options = new SharpLinkAdmissionControlOptions
            {
                MaxQueuedCalls = 4,
                MaxQueuedBytes = 1024,
                MaxQueueDelay = TimeSpan.FromSeconds(10)
            };
            options.Global.UseConcurrency(1);
            var controller = SharpLinkAdmissionController.Create(options, [], clock);
            var context = new SharpLinkAdmissionContext(1, 2, RpcMethodKind.Unary, "queued", null, null);
            var held = LongRunningTestWorker.WaitForPhase(
                controller.AcquireAsync(context, 1, allowQueue: true, CancellationToken.None).AsTask(), Bound);
            var call = ServerCallCancellationState.Rent(42, default, clock, connection.ConnectionToken,
                CancellationToken.None, supportsCooperativeCancellation: true);
            connection.CallCancellations.Set(42, call);
            var queued = controller.AcquireAsync(context, 1, allowQueue: true, call.InvocationToken).AsTask();
            Ensure(!queued.IsCompleted, "the ordinary admission request must actually be queued");
            using var releaseContinuation = new ManualResetEventSlim();
            var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cleanup = LongRunningTestWorker.Run(() =>
            {
                try
                {
                    try
                    {
                        LongRunningTestWorker.WaitForPhase(queued, Bound);
                        throw new Exception("closed connection must cancel its queued admission");
                    }
                    catch (OperationCanceledException) { }
                    cancellationObserved.SetResult();
                    Ensure(releaseContinuation.Wait(Bound), "release the already-cancelled admission continuation");
                }
                finally
                {
                    connection.CallCancellations.TryRemove(42, out _);
                    call.Dispose();
                }
            });
            try
            {
                LongRunningTestWorker.WaitForPhase(connection.CloseAsync().AsTask(), Bound);
                LongRunningTestWorker.WaitForPhase(cancellationObserved.Task, Bound);
                Ensure(connection.ActiveCalls == 0 && connection.CallCancellations.Count == 1,
                    "a cancelled queued admission can remain published after all active calls drain");
                Ensure(!connection.ServiceCleanupTask.IsFaulted,
                    "ordinary queued shutdown must wait, not invoke the strict empty-map certificate prematurely");
                releaseContinuation.Set();
                LongRunningTestWorker.WaitForPhase(cleanup, Bound);
                LongRunningTestWorker.WaitForPhase(connection.ServiceCleanupTask, Bound);
                Ensure(service.DisposeCount == 1, "queued shutdown must eventually clean the connection exactly once");
            }
            finally
            {
                releaseContinuation.Set();
                LongRunningTestWorker.Join(cleanup, Bound);
                held.Lease!.Dispose();
                LongRunningTestWorker.Join(connection.CloseAsync().AsTask(), Bound);
                LongRunningTestWorker.Join(connection.ServiceCleanupTask, Bound);
                LongRunningTestWorker.Join(controller.DisposeAsync().AsTask(), Bound);
                LongRunningTestWorker.Join(registration.DisposeAsync().AsTask(), Bound);
            }
        });
}
