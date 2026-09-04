namespace SharpLink.UnitTests.Abstractions;

public class SharpLinkExceptionTests
{
    [Test]
    public async Task NonWireErrorCodesMustBeRejectedAtConstruction()
    {
        var unknown = Capture(() => new SharpLinkException(SharpLinkErrorCode.Unknown, "unknown"));
        var undefined = Capture(() => new SharpLinkException((SharpLinkErrorCode)int.MaxValue, "undefined"));
        var concrete = Capture(() => new SharpLinkException(SharpLinkErrorCode.Internal, "valid"));

        await Assert.That(unknown).IsAssignableTo<ArgumentOutOfRangeException>();
        await Assert.That(undefined).IsAssignableTo<ArgumentOutOfRangeException>();
        await Assert.That(concrete).IsNull();
    }

    [Test]
    public async Task DetailCodeShouldBeExposedWithoutChangingMessageSemantics()
    {
        var exception = new SharpLinkException(
            SharpLinkErrorCode.ResourceExhausted,
            SharpLinkErrorDetails.ResourceExhausted.AdmissionQueue,
            "Admission queue is full.");

        await Assert.That(exception.Code).IsEqualTo(SharpLinkErrorCode.ResourceExhausted);
        await Assert.That(exception.DetailCode)
            .IsEqualTo(SharpLinkErrorDetails.ResourceExhausted.AdmissionQueue);
        await Assert.That(exception.Message).IsEqualTo("Admission queue is full.");
    }

    [Test]
    public async Task ExistingConstructorsShouldUseUnspecifiedDetailCode()
    {
        var exception = new SharpLinkException(
            SharpLinkErrorCode.Unavailable,
            "Temporarily unavailable.");

        await Assert.That(exception.DetailCode).IsEqualTo(SharpLinkErrorDetails.Unspecified);
    }

    [Test]
    public async Task UnknownDetailCodeShouldRemainObservable()
    {
        const ushort futureDetail = ushort.MaxValue;
        var exception = new SharpLinkException(
            SharpLinkErrorCode.ResourceExhausted,
            futureDetail,
            "A newer peer supplied an unknown detail.");

        await Assert.That(exception.DetailCode).IsEqualTo(futureDetail);
    }

    private static Exception? Capture(Action action)
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
}
