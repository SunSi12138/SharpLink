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
