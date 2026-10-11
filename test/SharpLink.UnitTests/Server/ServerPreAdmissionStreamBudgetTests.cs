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

        Ensure(governor.TryReservePreAdmissionStreamBytes(6), "first stream-byte reservation");
        Ensure(governor.TryReservePreAdmissionStreamBytes(4), "second stream-byte reservation");
        Ensure(governor.PreAdmissionStreamBytes == 10,
            "two callers must share one global stream-byte budget");
        Ensure(!governor.TryReservePreAdmissionStreamBytes(1),
            "another caller must not receive a private budget after the global limit is full");

        governor.ReleasePreAdmissionStreamBytes(6);
        Ensure(governor.PreAdmissionStreamBytes == 4,
            "release of one physical stream buffer must return only its byte ownership");

        Ensure(governor.TryReservePreAdmissionStreamBytes(6),
            "released capacity must be immediately reusable");
        Ensure(governor.PreAdmissionStreamBytes == 10,
            "replacement ownership must refill the shared limit exactly");

        governor.ReleasePreAdmissionStreamBytes(6);
        governor.ReleasePreAdmissionStreamBytes(4);
        Ensure(governor.PreAdmissionStreamBytes == 0,
            "all stream-buffer ownership must return to zero");
        Ensure(CaptureFailure(() => governor.ReleasePreAdmissionStreamBytes(4))
               is InvalidOperationException,
            "the production release path must reject duplicate release instead of underflowing");
        Ensure(governor.PreAdmissionStreamBytes == 0,
            "rejected duplicate release must restore the physical byte counter");
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
