using System.Diagnostics;

namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private ServerRequestEnvelope ReadRequestEnvelope(
        IRpcSession session,
        ReadOnlySequence<byte> payload,
        ProtocolV2FrameFlags flags)
    {
        var utcNow = DateTimeOffset.UtcNow;
        var monotonicNow = Stopwatch.GetTimestamp();
        return ServerRequestEnvelopeReader.Read(
            session,
            payload,
            flags,
            _protocolOptions.MaxMetadataBytes,
            utcNow,
            monotonicNow);
    }

    private static bool IsDeadlineExceeded(long deadlineTimestamp)
        => deadlineTimestamp > 0 && deadlineTimestamp <= Stopwatch.GetTimestamp();

}
