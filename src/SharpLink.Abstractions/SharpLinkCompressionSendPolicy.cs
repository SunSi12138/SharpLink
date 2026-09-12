namespace SharpLink.Abstractions;

/// <summary>Defines one immutable local outbound compression policy snapshot.</summary>
/// <remarks>
/// The direction is selected by the API that consumes this value: client request compression or
/// server response compression. It does not change provider advertisement or inbound decoding.
/// </remarks>
public sealed class SharpLinkCompressionSendPolicy
{
    /// <summary>Gets whether locally-sent business payloads may be adaptively compressed.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Gets the smallest business payload considered for compression.</summary>
    public int MinimumPayloadBytes { get; init; } = 1024;

    /// <summary>Gets the minimum absolute byte saving, including the original-length prefix.</summary>
    public int MinimumSavingsBytes { get; init; } = 64;

    /// <summary>Gets the minimum fractional saving in the inclusive range 0 through 1.</summary>
    public double MinimumSavingsRatio { get; init; } = 0.05;
}
