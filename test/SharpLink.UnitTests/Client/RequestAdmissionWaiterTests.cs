using System.Linq;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class RequestAdmissionWaiterTests
{
    [Test]
    public async Task CompletedSignalMustSurviveOtherGenerationsUntilConsumed()
    {
        var first = RequestAdmissionWaiter.Rent();
        var pending = first.WaitAsync();
        first.Complete(false);
        for (var index = 0; index < 1024; index++)
        {
            var next = RequestAdmissionWaiter.Rent();
            Ensure(!ReferenceEquals(first, next), "unconsumed signal cannot be recycled");
            var accepted = next.WaitAsync();
            next.Complete(true);
            Ensure(await accepted, "next generation must retain its acceptance");
        }
        Ensure(!await pending, "delayed consumer must retain the original rejection");
    }

    [Test]
    public async Task IndependentPendingSlotMayReturnBeforeAdmissionConsumerResumes()
    {
        var call = PendingCall.Rent(null!, 1, PendingCallKind.ClientStreaming,
            null, null, default, default, null);
        var pending = call.WaitForRequestAdmissionAsync();
        call.RejectRequestAdmission();
        call.ReturnUnused();
        var replacement = PendingCall.Rent(null!, 2, PendingCallKind.ClientStreaming,
            null, null, default, default, null);
        try
        {
            replacement.TryAcceptRequest();
            Ensure(await replacement.WaitForRequestAdmissionAsync(), "replacement is admitted");
            Ensure(!await pending, "slot recycling cannot admit the previous producer");
        }
        finally
        {
            replacement.ReturnUnused();
        }
    }

    [Test]
    public async Task ConcurrentCompletionAndConsumptionMustNotResetALiveSignal()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async worker =>
        {
            for (var index = 0; index < 1024; index++)
            {
                var waiter = RequestAdmissionWaiter.Rent();
                var pending = waiter.WaitAsync();
                var expected = (index + worker) % 2 == 0;
                var completion = Task.Run(() => waiter.Complete(expected));
                Ensure(await pending == expected, "result belongs to its own generation");
                await completion;
            }
        }));
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
