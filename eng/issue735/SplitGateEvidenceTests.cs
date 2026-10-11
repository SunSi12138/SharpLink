using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Runtime;

namespace SharpLink.UnitTests.Runtime;

// Copied unchanged into the control and disposable D candidate. These are
// hypothesis tests for directional independence, not pre-existing dev contracts.
public sealed class SplitGateEvidenceTests
{
    [Test]
    public async Task SendAdmissionCanProgressWhileReceiveDomainIsHeld()
    {
        var controller = new StreamFlowController(16, 32, 128, 4);
        var receive = Gate(controller, "_receiveGate", "_gate");
        await RunWithOtherDomainHeld(receive, () =>
        {
            Ensure(controller.TryAcquireSendCredit(1, 0, 16), "send admission was rejected");
            controller.ApplyWindowUpdate(1, 0, 32);
            Ensure(controller.SendConnectionCredit == 32, "#840 effective credit changed");
            controller.CompleteSendStream(1, 0);
        });
    }

    [Test]
    public async Task ReceiveConsumptionCanProgressWhileSendDomainIsHeld()
    {
        var controller = new StreamFlowController(16, 32, 128, 4);
        var send = Gate(controller, "_gate", "_gate");
        await RunWithOtherDomainHeld(send, () =>
        {
            controller.AcceptReceived(1, 0, 8);
            Ensure(controller.RecordConsumed(1, 0, 8) == 8, "receive threshold changed");
            Ensure(controller.FlushConsumed(1, 0) == 0, "receive credit double return");
        });
    }

    [Test]
    public async Task ConcurrentDirectionChurnAndTerminalPreserveOriginalException()
    {
        for (var round = 0; round < 32; round++)
        {
            var controller = new StreamFlowController(16, 32, 128, 4);
            using var start = new ManualResetEventSlim();
            var expected = new InvalidOperationException("same terminal identity");
            var send = Task.Run(() =>
            {
                start.Wait();
                for (var i = 0; i < 128; i++)
                {
                    try
                    {
                        controller.TryAcquireSendCredit(1, 0, 16);
                        controller.ApplyWindowUpdate(1, 0, 16);
                        controller.CompleteSendStream(1, 0);
                    }
                    catch (Exception e) when (ReferenceEquals(e, expected)) { return; }
                }
            });
            var receive = Task.Run(() =>
            {
                start.Wait();
                for (var i = 0; i < 128; i++)
                {
                    try
                    {
                        controller.AcceptReceived(1, 0, 8);
                        controller.RecordConsumed(1, 0, 8);
                        controller.FlushConsumed(1, 0);
                    }
                    catch (Exception e) when (ReferenceEquals(e, expected)) { return; }
                }
            });
            var close = Task.Run(() => { start.Wait(); controller.Complete(expected); });
            start.Set();
            await Task.WhenAll(send, receive, close).WaitAsync(TimeSpan.FromSeconds(10));
            Ensure(controller.ActiveSendStreamCount == 0 && controller.RetainedSendStreamCount == 0,
                "terminal retained send state");
            ExpectOriginal(() => controller.TryAcquireSendCredit(2, 0, 1), expected);
            ExpectOriginal(() => controller.AcceptReceived(2, 0, 1), expected);
            controller.Complete(new Exception("second close"));
            ExpectOriginal(() => controller.AcceptReceived(2, 0, 1), expected);
        }
    }

    private static async Task RunWithOtherDomainHeld(Lock gate, Action action)
    {
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Factory.StartNew(() =>
        {
            using (gate.EnterScope())
            {
                held.Set();
                release.Wait();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task? operation = null;
        try
        {
            Ensure(held.Wait(TimeSpan.FromSeconds(10)), "holder did not enter the domain");
            operation = Task.Factory.StartNew(action, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(10));
            if (operation is not null) await operation.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static Lock Gate(StreamFlowController controller, string name, string fallback)
        => (Lock)(typeof(StreamFlowController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                  ?? typeof(StreamFlowController).GetField(fallback, BindingFlags.Instance | BindingFlags.NonPublic))!
            .GetValue(controller)!;

    private static void ExpectOriginal(Action action, Exception expected)
    {
        try { action(); }
        catch (Exception e) when (ReferenceEquals(e, expected)) { return; }
        throw new InvalidOperationException("terminal exception identity was not preserved");
    }

    private static void Ensure(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
