using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public class AdmissionDeadlineWaitArbitrationTests
{
    [Test]
    public async Task ExpiredDeadlineShouldWinLaterCallerCancellationWithoutTimerCallback()
    {
        var timeProvider = new ManualTimeProvider();
        var options = new SharpLinkAdmissionControlOptions
        {
            MaxQueuedCalls = 1,
            MaxQueuedBytes = 1024,
            MaxQueueDelay = TimeSpan.FromSeconds(10)
        };
        options.Global.UseConcurrency(1);
        await using var controller = SharpLinkAdmissionController.Create(options, [], timeProvider);
        var context = new SharpLinkAdmissionContext(
            1,
            2,
            RpcMethodKind.Unary,
            "connection",
            authenticationContext: null,
            metadata: null);
        var first = await controller.AcquireAsync(
            context,
            retainedBytes: 1,
            allowQueue: true,
            CancellationToken.None);
        Ensure(first.IsAcquired, "the first call must hold the admission permit");

        using var callerCancellation = new CancellationTokenSource();
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(5), timeProvider);
        var pending = controller.AcquireAsync(
            context,
            retainedBytes: 64,
            allowQueue: true,
            deadline,
            callerCancellation.Token);
        Ensure(!pending.IsCompleted, "the second call must be blocked in admission");

        timeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(5));
        callerCancellation.Cancel();

        var rejected = await pending;
        Ensure(!rejected.IsAcquired &&
               rejected.ErrorCode == SharpLinkErrorCode.DeadlineExceeded &&
               rejected.Reason == "deadline",
            "the expired frozen deadline must win over later caller cancellation");
        Ensure(controller.QueuedCalls == 0 && controller.QueuedBytes == 0,
            "deadline arbitration must release all admission queue accounting");
        Ensure(controller.ActivePermits == 1,
            "deadline arbitration must not steal the permit held by the first call");

        first.Lease!.Dispose();
        Ensure(controller.ActivePermits == 0,
            "disposing the first admission lease must release the final permit");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
