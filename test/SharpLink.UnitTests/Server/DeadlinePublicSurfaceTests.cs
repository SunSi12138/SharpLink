using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public class DeadlinePublicSurfaceTests
{
    [Test]
    public void AbsoluteDeadlineCompatibilityPropertiesShouldNotRemainPublic()
    {
        Ensure(typeof(SharpLinkCallContextSnapshot).GetProperty("Deadline") is null,
            "call context must not retain the old absolute Deadline property");
        Ensure(typeof(SharpLinkAdmissionContext).GetProperty("Deadline") is null,
            "admission context must not retain the old absolute Deadline property");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
