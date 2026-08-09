using System.Diagnostics;

namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private RpcRequestEnvelope ReadRequestEnvelope(
        IRpcSession session,
        ReadOnlySequence<byte> payload,
        ProtocolV2FrameFlags flags)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out long interfaceHash) ||
            !reader.TryReadLittleEndian(out long methodHash))
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.ProtocolViolation,
                "Request routing prefix is truncated.");
        }

        DateTimeOffset? deadline = null;
        var deadlineTimestamp = 0L;
        if ((flags & ProtocolV2FrameFlags.HasDeadline) != 0)
        {
            if (!reader.TryReadLittleEndian(out long unixMilliseconds))
                throw new SharpLinkException(SharpLinkErrorCode.ProtocolViolation, "Request deadline is truncated.");
            try
            {
                deadline = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
                var utcNow = DateTimeOffset.UtcNow;
                var monotonicNow = Stopwatch.GetTimestamp();
                deadlineTimestamp = GetMonotonicDeadlineTimestamp(
                    deadline.Value,
                    utcNow,
                    monotonicNow);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.ProtocolViolation,
                    "Request deadline is outside the supported UTC range.",
                    exception);
            }
        }

        SharpLinkMetadata? metadata = null;
        if ((flags & ProtocolV2FrameFlags.HasMetadata) != 0)
        {
            if (session is not RpcSession runtimeSession ||
                (runtimeSession.NegotiatedCapabilities & ProtocolV2Capabilities.Metadata) == 0)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.ProtocolViolation,
                    "Request metadata was not negotiated during handshake.");
            }
            if (!ProtocolV2PayloadCodec.TryReadVarUInt32(ref reader, out var metadataLength) ||
                metadataLength > _protocolOptions.MaxMetadataBytes ||
                reader.Remaining < metadataLength)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.ProtocolViolation,
                    "Request metadata length is invalid.");
            }
            metadata = ProtocolV2PayloadCodec.ReadMetadata(
                reader.Sequence.Slice(reader.Position, metadataLength));
            reader.Advance(metadataLength);
        }

        return new RpcRequestEnvelope(
            interfaceHash,
            methodHash,
            reader.UnreadSequence,
            deadline,
            deadlineTimestamp,
            metadata);
    }

    private readonly record struct RpcRequestEnvelope(
        long InterfaceHash,
        long MethodHash,
        ReadOnlySequence<byte> Arguments,
        DateTimeOffset? Deadline,
        long DeadlineTimestamp,
        SharpLinkMetadata? Metadata);

    private static bool IsDeadlineExceeded(long deadlineTimestamp)
        => deadlineTimestamp > 0 && deadlineTimestamp <= Stopwatch.GetTimestamp();

    private static long GetMonotonicDeadlineTimestamp(
        DateTimeOffset deadline,
        DateTimeOffset utcNow,
        long monotonicNow)
    {
        var remaining = deadline - utcNow;
        if (remaining <= TimeSpan.Zero)
            return monotonicNow;
        var stopwatchTicks = remaining.TotalSeconds * Stopwatch.Frequency;
        if (stopwatchTicks >= long.MaxValue - monotonicNow)
            return long.MaxValue;
        return monotonicNow + Math.Max(1L, (long)Math.Ceiling(stopwatchTicks));
    }


}
