namespace SharpLink.Runtime;

/// <summary>
/// Trusted fixed-width serialization capability used only by the stream page path.
/// </summary>
/// <remarks>
/// Implement only on reviewed, sealed runtime primitive codecs whose existing Serialize
/// implementation always writes exactly <see cref="EncodedBytes"/> bytes, including nulls.
/// This is not a general sizing API or permission to bypass the selected codec. Custom,
/// generated, wrapper, enum and unsafe-blit codecs must use the ordinary stream path
/// unless their serialization contract is separately reviewed. Unary codec resolution
/// and serialization do not use this capability.
/// </remarks>
internal interface IStreamPageFixedSizeCodec
{
    int EncodedBytes { get; }
}
