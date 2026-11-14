#if SHARPLINK_775_STATIC_SLICE
namespace SharpLink.Runtime;

internal static partial class StaticBindingWire
{
    // Only this frame carries an independent OneWay input shape. Ordinary Request is unchanged.
    internal const ProtocolV2FrameType OneWayInputRequest = (ProtocolV2FrameType)245;
    internal static int ReadOneWayInputCount(in ReadOnlySequence<byte> payload)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryRead(out var count) || count is < 1 or > 127)
            throw Malformed("OneWay input count must be between 1 and 127.");
        return count;
    }
}
#endif
