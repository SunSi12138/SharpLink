using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public class ServerPreAdmissionStreamBudgetTests
{
    [Test]
    public void StreamBudgetShouldRemainGloballyBoundedAndReleaseExactlyOnce()
    {
        var governor = new ServerResourceGovernor(
            maxConcurrentDecodes: 1,
            maxRetainedCompressedBytes: 1024,
            maxDecodedBytesInFlight: 1024,
            maxPreAdmissionStreamBytes: 10);

        Ensure(governor.TryAcquirePreAdmissionStreamBytes(6, out var first) && first is not null,
            "first stream-byte permit");
        Ensure(governor.TryAcquirePreAdmissionStreamBytes(4, out var second) && second is not null,
            "second stream-byte permit");
        Ensure(governor.PreAdmissionStreamBytes == 10,
            "two callers must share one global stream-byte budget");
        Ensure(!governor.TryAcquirePreAdmissionStreamBytes(1, out var rejected) && rejected is null,
            "another caller must not receive a private budget after the global limit is full");

        first!.Dispose();
        first.Dispose();
        Ensure(governor.PreAdmissionStreamBytes == 4,
            "disposing a permit twice must release its physical ownership only once");

        Ensure(governor.TryAcquirePreAdmissionStreamBytes(6, out var replacement) && replacement is not null,
            "released capacity must be immediately reusable");
        Ensure(governor.PreAdmissionStreamBytes == 10,
            "replacement ownership must refill the shared limit exactly");

        replacement!.Dispose();
        second!.Dispose();
        Ensure(governor.PreAdmissionStreamBytes == 0,
            "all stream-buffer ownership must return to zero");
    }

    [Test]
    public void RawStreamBudgetCallbacksShouldRejectWithoutMutatingAccounting()
    {
        var governor = new ServerResourceGovernor(1, 1024, 1024, 8);

        Ensure(governor.TryReservePreAdmissionStreamBytes(5), "first raw stream reservation");
        Ensure(!governor.TryReservePreAdmissionStreamBytes(4),
            "over-budget raw reservation must reject");
        Ensure(governor.PreAdmissionStreamBytes == 5,
            "rejected raw reservation must leave accounting unchanged");

        governor.ReleasePreAdmissionStreamBytes(5);
        Ensure(governor.PreAdmissionStreamBytes == 0,
            "raw callback release must return the budget to zero");
    }

    [Test]
    public void StreamBudgetOptionShouldRequirePositiveValue()
    {
        var failure = CaptureFailure(new SharpLinkFlowControlOptions
        {
            MaxPreAdmissionStreamBytesPerServer = 0
        }.Validate);

        Ensure(failure is ArgumentOutOfRangeException,
            "pre-admission stream-byte budget must have a positive hard bound");
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

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }
}
