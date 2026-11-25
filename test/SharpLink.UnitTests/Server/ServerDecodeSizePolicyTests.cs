using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public class ServerDecodeSizePolicyTests
{
    private const int InlineBoundary = 1024 * 1024;
    private const int SmallPayload = 64 * 1024;

    [Test]
    [Arguments(InlineBoundary - 1, InlineBoundary - 1, false)]
    [Arguments(InlineBoundary, SmallPayload, true)]
    [Arguments(SmallPayload, InlineBoundary, true)]
    public async Task EitherInputOrOutputAtTheBoundaryRequiresPersistentDecode(
        int encodedBytes,
        int decodedBytes,
        bool expected)
    {
        await Assert.That(SharpLinkServer.MeetsPersistentDecodeSizeThreshold(encodedBytes, decodedBytes))
            .IsEqualTo(expected);
    }
}
