namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private bool TryPrepareCompressedRequestDecode(
        ServerRequestPermit requestPermit,
        ServerRetainedCompressedPermit? retainedCompressedPermit,
        ProtocolV2FrameFlags flags,
        ReadOnlySequence<byte> payload,
        out ServerDecodePermit? decodePermit,
        out SharpLinkException? rejection)
    {
        ArgumentNullException.ThrowIfNull(requestPermit);

        var acquired = retainedCompressedPermit is null
            ? requestPermit.TryAcquireDecodePermit(0, out decodePermit)
            : requestPermit.TryAcquireDecodePermit(retainedCompressedPermit, out decodePermit);
        if (!acquired || decodePermit is null)
        {
            rejection = CreateDecodeResourceExhaustion(
                SharpLinkResourceExhaustion.ServerDecodeConcurrency,
                "Server decode concurrency is exhausted.");
            return false;
        }

        var decodedPayloadBytes = RpcSession.ReadCompressedDecodedPayloadLength(
            ProtocolV2FrameType.Request,
            flags,
            payload);
        if (!decodePermit.TryReserveDecodedBytes(decodedPayloadBytes))
        {
            rejection = CreateDecodeResourceExhaustion(
                SharpLinkResourceExhaustion.ServerDecodedBytes,
                "Server decoded request byte budget is exhausted.");
            return false;
        }

        rejection = null;
        return true;
    }

    private static SharpLinkException CreateDecodeResourceExhaustion(
        string reason,
        string message)
    {
        SharpLinkTelemetry.RecordResourceExhausted("server", reason);
        return SharpLinkResourceExhaustion.CreateWire(
            reason,
            $"{message} ({reason}).");
    }

    private static SharpLinkException CreateDecodeQueueResourceExhaustion()
    {
        const string reason = SharpLinkResourceExhaustion.ServerDecodeQueue;
        SharpLinkTelemetry.RecordResourceExhausted("server", reason);
        return SharpLinkResourceExhaustion.CreateWire(
            reason,
            $"Server persistent decode queue is exhausted ({reason}).");
    }

    private static SharpLinkException CreateRetainedCompressedResourceExhaustion()
    {
        const string reason = SharpLinkResourceExhaustion.ServerRetainedCompressedBytes;
        SharpLinkTelemetry.RecordResourceExhausted("server", reason);
        return SharpLinkResourceExhaustion.CreateWire(
            reason,
            $"Server retained compressed request byte budget is exhausted ({reason}).");
    }
}
