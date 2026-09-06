namespace SharpLink.Runtime;

/// <summary>Synchronously compresses and decompresses complete SharpLink business payloads.</summary>
/// <remarks>
/// Implementations must be thread safe and must not retain input or output buffers after an operation completes.
/// A successful operation consumes the complete input payload. Providers must not silently accept trailing compressed
/// data, and every decode-relevant setting must be represented by <see cref="WireProfile"/>. The provider owns the
/// integrity semantics of its wire profile; SharpLink Core does not add algorithm-specific framing or checksums.
/// </remarks>
public interface ISharpLinkCompressionProvider
{
    /// <summary>
    /// Gets the stable, case-sensitive wire-profile token advertised during the handshake.
    /// Every setting required for successful decoding, such as a dictionary identity, must be represented by this token.
    /// Encode-only tuning may differ between peers when it does not change decode compatibility.
    /// </summary>
    string WireProfile { get; }

    /// <summary>Attempts to compress one complete single- or multi-segment business payload.</summary>
    /// <param name="input">The complete uncompressed business payload. Returning <see langword="true"/> means all input was consumed.</param>
    /// <param name="output">The temporary output owned by SharpLink for the duration of this call.</param>
    /// <param name="maxOutputBytes">The maximum number of bytes the complete compressed representation may write.</param>
    /// <param name="cancellationToken">Cancels provider work before the frame is queued.</param>
    /// <returns>
    /// <see langword="true"/> when a complete representation was written; <see langword="false"/> only when the
    /// provider cannot produce the complete representation within <paramref name="maxOutputBytes"/>. SharpLink
    /// discards the temporary output and sends the original payload in that case. A provider must not return
    /// <see langword="false"/> merely because it considers the compression ratio unprofitable; that policy belongs to Core.
    /// </returns>
    bool TryCompress(
        ReadOnlySequence<byte> input,
        IBufferWriter<byte> output,
        int maxOutputBytes,
        CancellationToken cancellationToken = default);

    /// <summary>Decompresses one complete single- or multi-segment compressed payload.</summary>
    /// <param name="input">
    /// The complete compressed representation. A normal return means all input was consumed and no trailing bytes were ignored.
    /// </param>
    /// <param name="output">The output owned by SharpLink for the duration of this call.</param>
    /// <param name="maxOutputBytes">The maximum permitted decompressed size.</param>
    /// <param name="cancellationToken">Cancels decompression.</param>
    /// <exception cref="InvalidDataException">
    /// The representation is malformed, truncated, contains trailing data, fails profile integrity validation, or would
    /// exceed <paramref name="maxOutputBytes"/>.
    /// </exception>
    void Decompress(
        ReadOnlySequence<byte> input,
        IBufferWriter<byte> output,
        int maxOutputBytes,
        CancellationToken cancellationToken = default);
}

/// <summary>Configures negotiated payload compression for one runtime context.</summary>
/// <example>
/// <code>
/// builder.UseRuntime(options =&gt;
/// {
///     options.Compression.Providers.Add(new MyCompressionProvider());
///     options.Compression.MinimumPayloadBytes = 2048;
/// });
/// </code>
/// </example>
public sealed class SharpLinkCompressionOptions
{
    private IReadOnlyList<SharpLinkCompressionProviderBinding>? _providerBindings;

    /// <summary>The maximum number of advertised providers.</summary>
    public const int MaxProviders = 16;

    /// <summary>
    /// Gets wire-profile providers in local preference order. An empty list completely disables compression.
    /// Multiple configurations may participate in negotiation when they expose distinct tokens.
    /// </summary>
    public IList<ISharpLinkCompressionProvider> Providers { get; } = new List<ISharpLinkCompressionProvider>();

    /// <summary>Gets or sets the smallest business payload considered for compression.</summary>
    public int MinimumPayloadBytes { get; set; } = 1024;

    /// <summary>Gets or sets the minimum absolute byte saving, including the original-length prefix.</summary>
    public int MinimumSavingsBytes { get; set; } = 64;

    /// <summary>Gets or sets the minimum fractional saving in the inclusive range 0 through 1.</summary>
    public double MinimumSavingsRatio { get; set; } = 0.05;

    /// <summary>Validates provider tokens and compression-benefit thresholds.</summary>
    public void Validate()
        => _ = ValidateAndCreateBindings();

    private List<SharpLinkCompressionProviderBinding> ValidateAndCreateBindings()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MinimumPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(MinimumSavingsBytes);
        if (double.IsNaN(MinimumSavingsRatio) || MinimumSavingsRatio is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(MinimumSavingsRatio));
        if (Providers.Count > MaxProviders)
            throw new ArgumentOutOfRangeException(nameof(Providers), $"At most {MaxProviders} providers may be configured.");

        var profiles = new HashSet<string>(StringComparer.Ordinal);
        var bindings = new List<SharpLinkCompressionProviderBinding>(Providers.Count);
        foreach (var provider in Providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            var wireProfile = provider.WireProfile;
            SharpLinkCompressionProfile.Validate(wireProfile, nameof(Providers));
            if (!profiles.Add(wireProfile))
                throw new ArgumentException($"Compression wire profile '{wireProfile}' is registered more than once.", nameof(Providers));
            bindings.Add(new SharpLinkCompressionProviderBinding(wireProfile, provider));
        }
        return bindings;
    }

    internal SharpLinkCompressionOptions CloneValidated()
    {
        var bindings = _providerBindings ?? ValidateAndCreateBindings();
        var clone = new SharpLinkCompressionOptions
        {
            MinimumPayloadBytes = MinimumPayloadBytes,
            MinimumSavingsBytes = MinimumSavingsBytes,
            MinimumSavingsRatio = MinimumSavingsRatio,
            _providerBindings = bindings
        };
        foreach (var provider in Providers)
            clone.Providers.Add(provider);
        return clone;
    }

    internal IReadOnlyList<SharpLinkCompressionProviderBinding> ProviderBindings
        => _providerBindings ?? throw new InvalidOperationException(
            "Compression provider bindings are available only after options are frozen.");

    internal void CopyValidatedSnapshotTo(SharpLinkCompressionOptions destination)
    {
        destination.MinimumPayloadBytes = MinimumPayloadBytes;
        destination.MinimumSavingsBytes = MinimumSavingsBytes;
        destination.MinimumSavingsRatio = MinimumSavingsRatio;
        foreach (var provider in Providers)
            destination.Providers.Add(provider);
        destination._providerBindings = ProviderBindings;
    }

    internal bool IsBeneficial(int originalBytes, int compressedBytes)
    {
        if (originalBytes < MinimumPayloadBytes || compressedBytes >= originalBytes)
            return false;
        var savings = originalBytes - compressedBytes;
        return savings >= MinimumSavingsBytes && savings >= originalBytes * MinimumSavingsRatio;
    }

    internal SharpLinkCompressionProviderBinding? FindProviderBinding(string wireProfile)
    {
        foreach (var binding in ProviderBindings)
        {
            if (string.Equals(binding.WireProfile, wireProfile, StringComparison.Ordinal))
                return binding;
        }
        return null;
    }
}

internal readonly record struct SharpLinkCompressionProviderBinding(
    string WireProfile,
    ISharpLinkCompressionProvider Provider);

internal static class SharpLinkCompressionProfile
{
    internal const int MaxAsciiBytes = 64;

    internal static void Validate(string? wireProfile, string parameterName)
    {
        if (string.IsNullOrEmpty(wireProfile) || wireProfile.Length > MaxAsciiBytes)
            throw new ArgumentException("Compression wire profiles must contain 1 to 64 ASCII bytes.", parameterName);
        foreach (var character in wireProfile)
        {
            if (character is < (char)0x21 or > (char)0x7e)
                throw new ArgumentException("Compression wire profiles must use canonical visible ASCII bytes.", parameterName);
        }
    }
}
