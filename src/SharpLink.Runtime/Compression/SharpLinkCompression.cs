using System.IO.Compression;

namespace SharpLink.Runtime;

/// <summary>Reports exactly how much input and output a compression provider processed.</summary>
/// <param name="ConsumedBytes">The number of compressed or uncompressed input bytes consumed.</param>
/// <param name="WrittenBytes">The number of bytes written to the bounded output.</param>
public readonly record struct SharpLinkCompressionResult(int ConsumedBytes, int WrittenBytes);

/// <summary>Synchronously compresses and decompresses SharpLink business payloads without reflection.</summary>
/// <remarks>
/// Implementations must be thread safe. They must not retain input or output buffers after an operation completes,
/// and must honor the operation's maximum output byte count before writing.
/// </remarks>
public interface ISharpLinkCompressionProvider
{
    /// <summary>
    /// Gets the stable, case-sensitive wire-profile token advertised during the handshake.
    /// Every setting required for successful decoding, such as a dictionary identity, must be represented by this token.
    /// Encode-only tuning such as compression level may differ between peers using the same token.
    /// </summary>
    string WireProfile { get; }

    /// <summary>Compresses one single- or multi-segment business payload into a bounded output.</summary>
    /// <param name="input">The complete uncompressed business payload.</param>
    /// <param name="output">The output owned by SharpLink for the duration of this call.</param>
    /// <param name="maxOutputBytes">The maximum number of bytes the provider may write.</param>
    /// <param name="cancellationToken">Cancels provider work before the frame is queued.</param>
    SharpLinkCompressionResult Compress(
        ReadOnlySequence<byte> input,
        IBufferWriter<byte> output,
        int maxOutputBytes,
        CancellationToken cancellationToken = default);

    /// <summary>Decompresses one single- or multi-segment business payload into a bounded output.</summary>
    /// <param name="input">The complete compressed business payload.</param>
    /// <param name="output">The output owned by SharpLink for the duration of this call.</param>
    /// <param name="maxOutputBytes">The maximum permitted decompressed size.</param>
    /// <param name="cancellationToken">Cancels decompression.</param>
    SharpLinkCompressionResult Decompress(
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
///     options.Compression.Providers.Add(SharpLinkCompressionProviders.CreateBrotli());
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

    internal ISharpLinkCompressionProvider? FindProvider(string wireProfile)
        => FindProviderBinding(wireProfile)?.Provider;

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

/// <summary>Creates the NativeAOT-safe Brotli provider backed only by <see cref="System.IO.Compression"/>.</summary>
public static class SharpLinkCompressionProviders
{
    /// <summary>Creates a provider using <see cref="BrotliStream"/>.</summary>
    /// <param name="level">The local encoding preference. It is not negotiated and does not affect Brotli decoding compatibility.</param>
    public static ISharpLinkCompressionProvider CreateBrotli(CompressionLevel level = CompressionLevel.Fastest)
        => new BrotliCompressionProvider(ValidateLevel(level));

    private static CompressionLevel ValidateLevel(CompressionLevel level)
        => Enum.IsDefined(level) ? level : throw new ArgumentOutOfRangeException(nameof(level));
}

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

internal sealed class BrotliCompressionProvider(CompressionLevel level) : ISharpLinkCompressionProvider
{
    private const uint IntegrityMagic = 0x31504353; // "SCP1" in little endian.
    private const int IntegrityTrailerBytes = sizeof(uint) + sizeof(uint);

    public string WireProfile => "brotli";

    public SharpLinkCompressionResult Compress(
        ReadOnlySequence<byte> input,
        IBufferWriter<byte> output,
        int maxOutputBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegative(maxOutputBytes);
        if (input.Length > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(input));

        var outputStream = new BoundedBufferWriterStream(output, maxOutputBytes);
        using (var compressor = new BrotliStream(outputStream, level, leaveOpen: true))
        {
            foreach (var segment in input)
            {
                cancellationToken.ThrowIfCancellationRequested();
                compressor.Write(segment.Span);
            }
        }
        outputStream.WriteIntegrityTrailer(IntegrityMagic);
        return new SharpLinkCompressionResult(
            checked((int)input.Length), outputStream.WrittenBytes);
    }

    public SharpLinkCompressionResult Decompress(
        ReadOnlySequence<byte> input,
        IBufferWriter<byte> output,
        int maxOutputBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegative(maxOutputBytes);
        if (input.Length > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(input));

        if (input.Length <= IntegrityTrailerBytes)
            throw new InvalidDataException("Compressed payload integrity trailer is truncated.");
        Span<byte> trailer = stackalloc byte[IntegrityTrailerBytes];
        input.Slice(input.Length - IntegrityTrailerBytes).CopyTo(trailer);
        if (BinaryPrimitives.ReadUInt32LittleEndian(trailer) != IntegrityMagic)
            throw new InvalidDataException("Compressed payload integrity trailer is missing.");
        var compressedPayload = input.Slice(0, input.Length - IntegrityTrailerBytes);
        var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(trailer[sizeof(uint)..]);
        if (Crc32Accumulator.Compute(compressedPayload) != expectedChecksum)
            throw new InvalidDataException("Compressed payload integrity checksum does not match.");

        var written = DecompressBrotli(
            compressedPayload,
            output,
            maxOutputBytes,
            cancellationToken);
        return new SharpLinkCompressionResult(checked((int)input.Length), written);
    }

    private static int DecompressBrotli(
        ReadOnlySequence<byte> input,
        IBufferWriter<byte> output,
        int maxOutputBytes,
        CancellationToken cancellationToken)
    {
        byte[]? contiguous = null;
        ReadOnlySpan<byte> source;
        if (input.IsSingleSegment)
        {
            source = input.FirstSpan;
        }
        else
        {
            contiguous = input.ToArray();
            source = contiguous;
        }

        using var decoder = new BrotliDecoder();
        var consumed = 0;
        var written = 0;
        Span<byte> outputLimitProbe = stackalloc byte[1];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OperationStatus status;
            int consumedNow;
            int writtenNow;
            if (written < maxOutputBytes)
            {
                var capacity = Math.Min(8192, maxOutputBytes - written);
                var destination = output.GetSpan(capacity)[..capacity];
                status = decoder.Decompress(
                    source[consumed..],
                    destination,
                    out consumedNow,
                    out writtenNow);
                output.Advance(writtenNow);
                written += writtenNow;
            }
            else
            {
                status = decoder.Decompress(
                    source[consumed..],
                    outputLimitProbe,
                    out consumedNow,
                    out writtenNow);
                if (writtenNow != 0)
                    throw new SharpLinkCompressionOutputLimitException(maxOutputBytes);
            }
            consumed += consumedNow;

            switch (status)
            {
                case OperationStatus.Done:
                    if (consumed != source.Length)
                        throw new InvalidDataException("Compressed payload contains trailing data.");
                    return written;
                case OperationStatus.InvalidData:
                    throw new InvalidDataException("Brotli payload is invalid.");
                case OperationStatus.NeedMoreData when consumed == source.Length:
                    throw new InvalidDataException("Brotli payload is truncated.");
            }
            if (consumedNow == 0 && writtenNow == 0)
                throw new InvalidDataException("Brotli decoder made no progress.");
        }
    }

}

internal sealed class BoundedBufferWriterStream(IBufferWriter<byte> writer, int maxBytes) : Stream
{
    private Crc32Accumulator _checksum;
    internal int WrittenBytes { get; private set; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => WrittenBytes;
    public override long Position { get => WrittenBytes; set => throw new NotSupportedException(); }
    public override void Flush() { }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length > maxBytes - WrittenBytes)
            throw new SharpLinkCompressionOutputLimitException(maxBytes);
        writer.Write(buffer);
        _checksum.Append(buffer);
        WrittenBytes += buffer.Length;
    }

    internal void WriteIntegrityTrailer(uint magic)
    {
        Span<byte> trailer = stackalloc byte[sizeof(uint) + sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, magic);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[sizeof(uint)..], _checksum.Value);
        if (trailer.Length > maxBytes - WrittenBytes)
            throw new SharpLinkCompressionOutputLimitException(maxBytes);
        writer.Write(trailer);
        WrittenBytes += trailer.Length;
    }

    public override void Write(byte[] buffer, int offset, int count)
        => Write(buffer.AsSpan(offset, count));

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

internal sealed class SharpLinkCompressionOutputLimitException(int maxBytes)
    : IOException($"Compressed payload exceeds its {maxBytes}-byte output limit.");

internal struct Crc32Accumulator
{
    private static readonly uint[] STable = CreateTable();
    private uint _state;
    private bool _initialized;

    internal uint Value => ~(_initialized ? _state : uint.MaxValue);

    internal void Append(ReadOnlySpan<byte> bytes)
    {
        var crc = _initialized ? _state : uint.MaxValue;
        _initialized = true;
        foreach (var value in bytes)
            crc = STable[(crc ^ value) & 0xff] ^ (crc >> 8);
        _state = crc;
    }

    internal static uint Compute(ReadOnlySequence<byte> sequence)
    {
        var accumulator = new Crc32Accumulator();
        foreach (var segment in sequence)
            accumulator.Append(segment.Span);
        return accumulator.Value;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1;
            table[index] = value;
        }
        return table;
    }
}
