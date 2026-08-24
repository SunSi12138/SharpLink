using System.Net;
using System.Threading;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionRuntimeControlRaceTests
{
    [Test]
    [NotInParallel]
    public async Task DisableRacingStopShouldNotDeadlockOrPublishAfterSeal()
    {
        await using var server = CreateServer();
        var publicServer = (ISharpLinkServer)server;
        publicServer.EnableAdmissionControl(options => options.Global.UseConcurrency(1));
        var original = server.CurrentAdmissionProgramForTests
            ?? throw new Exception("test requires an enabled publication");
        var kernel = original.Kernel;
        using var disableAtWriter = new ManualResetEventSlim();
        using var releaseDisable = new ManualResetEventSlim();

        try
        {
            SharpLinkServer.BeforeAdmissionPublicationLockForTests = (owner, candidate) =>
            {
                if (!ReferenceEquals(owner, server) || candidate is not null)
                    return;
                disableAtWriter.Set();
                if (!releaseDisable.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("disable-vs-Stop writer release timed out");
            };

            var disableTask = Task.Run(() => CaptureFailure(publicServer.DisableAdmissionControl));
            Ensure(disableAtWriter.Wait(TimeSpan.FromSeconds(5)),
                "Disable must reach the deterministic pre-writer barrier");

            var stopTask = server.StopAsync(TimeSpan.Zero).AsTask();
            await WaitUntilAsync(() => kernel.IsDraining,
                "Stop must seal the stable admission kernel before Disable resumes");
            releaseDisable.Set();

            var disableFailure = await disableTask.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(disableFailure is InvalidOperationException,
                "Disable linearized after Stop seal must reject deterministically");
            await stopTask.WaitAsync(TimeSpan.FromSeconds(5));

            Ensure(original.IsRetired && original.IsReclaimed && original.ReclaimCount == 1,
                "Stop must retire and reclaim the pre-seal generation exactly once");
            Ensure(kernel.IsDraining && kernel.LiveProgramCount == 0 &&
                   kernel.RetiredProgramCount == 0 && kernel.RuleStateCount == 0 &&
                   kernel.PartitionStateCount == 0 && kernel.QueuedCalls == 0 &&
                   kernel.QueuedBytes == 0 && kernel.ActivePermits == 0,
                "Disable-vs-Stop must finish without deadlock, publication, or residual accounting");
        }
        finally
        {
            SharpLinkServer.BeforeAdmissionPublicationLockForTests = null;
            releaseDisable.Set();
        }
    }

    private static SharpLinkServer CreateServer()
    {
        var builder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback.ToString());
        return (SharpLinkServer)builder.Build();
    }

    private static Exception? CaptureFailure(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (!condition())
                await Task.Delay(10, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new Exception($"assert failed: {scenario}");
        }
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }
}
