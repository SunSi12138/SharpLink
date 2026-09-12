namespace SharpLink.Runtime;

/// <summary>
/// Lets a response-stream dispatcher submit each user-visible delivery boundary to the owning
/// logical call before a buffered item becomes observable. Implementations may publish a local
/// terminal (for example DeadlineExceeded) when progress is no longer allowed.
/// </summary>
internal interface IStreamConsumerDeliveryGate
{
    bool TryAcceptStreamDelivery(long requestId);
}
