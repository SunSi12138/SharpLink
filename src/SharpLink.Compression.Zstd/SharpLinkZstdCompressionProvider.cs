using System;
using System.Buffers;
using System.IO;
using System.Threading;
using SharpLink.Runtime;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace SharpLink.Compression.Zstd;

/// <summary>
/// Provides the official SharpLink Zstandard wire profile for .NET 10 by using ZstdSharp.Port.
/// </summary>
/// <remarks>
/// The profile is exactly one RFC 8878 Zstandard frame, requires the standard frame checksum,
/// forbids dictionaries and trailing data, and limits the frame window to <see cref="WindowLog2"/>.
/// Compression quality is encode-only tuning and does not change the wire profile.
/// </remarks>
public sealed class SharpLinkZstdCompressionProvider : ISharpLinkCompressionProvider
{
    /// <summary>The stable SharpLink Zstandard wire profile.</summary>
    public const string Profile = "zstd-rfc8878-w23-checksum/v1";

    /// <summary>The maximum base-2 Zstandard window logarithm accepted by this profile (8 MiB).</summary>
    public const int WindowLog2 = 23;

    /// <summary>The default Zstandard compression level.</summary>
    public const int DefaultCompressionLevel = Compressor.DefaultCompressionLevel;

    private const int InputChunkBytes = 64 * 1024;
    private const int OutputChunkBytes = 64 * 1024;

    /// <summary>Initializes a provider using the default Zstandard compression level.</summary>
    public SharpLinkZstdCompressionProvider()
        : this(DefaultCompressionLevel)
    {
    }

    /// <summary>Initializes a provider using encode-only Zstandard compression tuning.</summary>
    /// <param name="compressionLevel">The Zstandard compression level. It does not change <see cref="WireProfile"/>.</param>
    public SharpLinkZstdCompressionProvider(int compressionLevel)
    {
        if (compressionLevel < Compressor.MinCompressionLevel || compressionLevel > Compressor.MaxCompressionLevel)
        {
            throw new ArgumentOutOfRangeException(
                nameof(compressionLevel),
                compressionLevel,
                $"Zstandard compression level must be between {Compressor.MinCompressionLevel} and {Compressor.MaxCompressionLevel}.");
        }
        CompressionLevel = compressionLevel;
    }

    /// <summary>Gets the encode-only Zstandard compression level.</summary>
    public int CompressionLevel { get; }

    /// <inheritdoc />
    public string WireProfile => Profile;

    /// <inheritdoc />
    public bool TryCompress(
        ReadOnlySequence<byte> input,
        IBufferWriter<byte> output,
        int maxOutputBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegative(maxOutputBytes);
        cancellationToken.ThrowIfCancellationRequested();

        using var compressor = new Compressor(CompressionLevel);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, WindowLog2);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
        compressor.SetPledgedSrcSize(checked((ulong)input.Length));

        var budget = new OutputBudget(output, maxOutputBytes);
        foreach (var segment in input)
        {
            var source = segment.Span;
            while (!source.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!budget.TryGetSpan(out var destination))
                    return false;

                var chunk = source[..Math.Min(InputChunkBytes, source.Length)];
                var status = compressor.WrapStream(
                    chunk,
                    destination,
                    out var consumed,
                    out var written,
                    isFinalBlock: false);
                budget.Advance(written);
                source = source[consumed..];
                if (!CanContinueCompression(status, consumed, written))
                    return false;
            }
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!budget.TryGetSpan(out var destination))
                return false;

            var status = compressor.FlushStream(destination, out var written, isFinalBlock: true);
            budget.Advance(written);
            if (status == OperationStatus.Done)
                return true;
            if (!CanContinueCompression(status, consumed: 0, written))
                return false;
        }
    }

    /// <inheritdoc />
    public void Decompress(
        ReadOnlySequence<byte> input,
        IBufferWriter<byte> output,
        int maxOutputBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegative(maxOutputBytes);
        cancellationToken.ThrowIfCancellationRequested();

        ZstdFrameValidator.Validate(input, maxOutputBytes);

        using var decompressor = new Decompressor();
        decompressor.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, WindowLog2);

        var budget = new OutputBudget(output, maxOutputBytes);
        Span<byte> overflowProbe = stackalloc byte[1];
        var completed = false;
        foreach (var segment in input)
        {
            var source = segment.Span;
            while (!source.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (budget.Remaining != 0)
                {
                    var destination = budget.GetSpan();
                    var status = decompressor.UnwrapStream(
                        source,
                        destination,
                        out var consumed,
                        out var written);
                    budget.Advance(written);
                    source = source[consumed..];
                    completed = InterpretDecompressionStatus(status, consumed, written, source.IsEmpty);
                }
                else
                {
                    var status = decompressor.UnwrapStream(
                        source,
                        overflowProbe,
                        out var consumed,
                        out var written);
                    if (written != 0)
                        throw new InvalidDataException("Zstandard payload exceeds its decompressed output limit.");
                    source = source[consumed..];
                    completed = InterpretDecompressionStatus(status, consumed, written, source.IsEmpty);
                }
            }
        }

        if (!completed)
        {
            var status = decompressor.UnwrapStream(
                ReadOnlySpan<byte>.Empty,
                overflowProbe,
                out var consumed,
                out var written);
            if (written != 0)
                throw new InvalidDataException("Zstandard payload exceeds its decompressed output limit.");
            completed = InterpretDecompressionStatus(status, consumed, written, sourceExhausted: true);
        }

        if (!completed)
            throw new InvalidDataException("Zstandard payload is truncated.");
    }

    private static bool InterpretDecompressionStatus(
        OperationStatus status,
        int consumed,
        int written,
        bool sourceExhausted)
    {
        return status switch
        {
            OperationStatus.Done => true,
            OperationStatus.InvalidData => throw new InvalidDataException("Zstandard payload is malformed or failed checksum validation."),
            OperationStatus.NeedMoreData when sourceExhausted => false,
            OperationStatus.NeedMoreData => false,
            OperationStatus.DestinationTooSmall when consumed != 0 || written != 0 => false,
            OperationStatus.DestinationTooSmall => throw new InvalidDataException("Zstandard decompression made no forward progress."),
            _ => throw new InvalidDataException($"Unexpected Zstandard decompression status '{status}'.")
        };
    }

    private static bool CanContinueCompression(OperationStatus status, int consumed, int written)
    {
        if (status == OperationStatus.InvalidData)
            throw new InvalidOperationException("Zstandard compressor rejected its configured state.");
        if (status == OperationStatus.DestinationTooSmall && consumed == 0 && written == 0)
            return false;
        if (status is not (OperationStatus.Done or OperationStatus.DestinationTooSmall) && consumed == 0 && written == 0)
            throw new InvalidOperationException("Zstandard compression made no forward progress.");
        return true;
    }

    private ref struct OutputBudget
    {
        private readonly IBufferWriter<byte> _output;
        private int _remaining;

        internal OutputBudget(IBufferWriter<byte> output, int maxOutputBytes)
        {
            _output = output;
            _remaining = maxOutputBytes;
        }

        internal int Remaining => _remaining;

        internal bool TryGetSpan(out Span<byte> span)
        {
            if (_remaining == 0)
            {
                span = default;
                return false;
            }
            span = GetSpan();
            return true;
        }

        internal Span<byte> GetSpan()
        {
            if (_remaining == 0)
                return Span<byte>.Empty;
            var sizeHint = Math.Min(OutputChunkBytes, _remaining);
            var span = _output.GetSpan(sizeHint);
            return span.Length > _remaining ? span[.._remaining] : span;
        }

        internal void Advance(int count)
        {
            if ((uint)count > (uint)_remaining)
                throw new InvalidOperationException("Zstandard provider exceeded the advertised output bound.");
            _output.Advance(count);
            _remaining -= count;
        }
    }
}

internal static class ZstdFrameValidator
{
    private const uint StandardFrameMagic = 0xFD2FB528;
    private const int WindowLogAbsoluteMinimum = 10;
    private const int ChecksumBytes = sizeof(uint);

    internal static void Validate(ReadOnlySequence<byte> input, int maxOutputBytes)
    {
        var reader = new SequenceReader<byte>(input);
        var magic = checked((uint)ReadUnsignedLittleEndian(ref reader, sizeof(uint), "frame magic"));
        if (magic != StandardFrameMagic)
            throw new InvalidDataException("Zstandard profile requires one standard RFC 8878 frame.");

        var descriptor = ReadByte(ref reader, "frame header descriptor");
        if ((descriptor & 0x18) != 0)
            throw new InvalidDataException("Zstandard frame uses reserved or non-canonical descriptor bits.");
        if ((descriptor & 0x04) == 0)
            throw new InvalidDataException("Zstandard profile requires the standard frame checksum.");
        if ((descriptor & 0x03) != 0)
            throw new InvalidDataException("Zstandard profile does not permit dictionaries.");

        var singleSegment = (descriptor & 0x20) != 0;
        var contentSizeFlag = descriptor >> 6;
        if (!singleSegment)
        {
            var windowDescriptor = ReadByte(ref reader, "window descriptor");
            var exponent = windowDescriptor >> 3;
            var mantissa = windowDescriptor & 0x07;
            var windowBase = 1L << (WindowLogAbsoluteMinimum + exponent);
            var windowSize = windowBase + (windowBase >> 3) * mantissa;
            if (windowSize > 1L << SharpLinkZstdCompressionProvider.WindowLog2)
                throw new InvalidDataException("Zstandard frame window exceeds the SharpLink profile limit.");
        }

        var frameContentSizeBytes = contentSizeFlag switch
        {
            0 => singleSegment ? 1 : 0,
            1 => 2,
            2 => 4,
            3 => 8,
            _ => throw new InvalidDataException("Zstandard frame content-size descriptor is invalid.")
        };
        if (frameContentSizeBytes != 0)
        {
            var contentSize = ReadUnsignedLittleEndian(ref reader, frameContentSizeBytes, "frame content size");
            if (frameContentSizeBytes == 2)
                contentSize += 256;
            if (contentSize > checked((ulong)maxOutputBytes))
                throw new InvalidDataException("Zstandard frame content size exceeds the decompressed output limit.");
            if (singleSegment && contentSize > 1UL << SharpLinkZstdCompressionProvider.WindowLog2)
                throw new InvalidDataException("Zstandard single-segment frame exceeds the SharpLink profile window limit.");
        }

        while (true)
        {
            var b0 = ReadByte(ref reader, "block header");
            var b1 = ReadByte(ref reader, "block header");
            var b2 = ReadByte(ref reader, "block header");
            var blockHeader = b0 | (b1 << 8) | (b2 << 16);
            var lastBlock = (blockHeader & 1) != 0;
            var blockType = (blockHeader >> 1) & 0x03;
            var blockSize = blockHeader >> 3;
            var encodedBytes = blockType switch
            {
                0 => blockSize,
                1 => 1,
                2 => blockSize,
                _ => throw new InvalidDataException("Zstandard frame contains a reserved block type.")
            };
            Advance(ref reader, encodedBytes, "block payload");
            if (lastBlock)
                break;
        }

        Advance(ref reader, ChecksumBytes, "frame checksum");
        if (reader.Remaining != 0)
            throw new InvalidDataException("Zstandard profile forbids trailing bytes and concatenated frames.");
    }

    private static byte ReadByte(ref SequenceReader<byte> reader, string field)
    {
        if (!reader.TryRead(out var value))
            throw new InvalidDataException($"Zstandard {field} is truncated.");
        return value;
    }

    private static ulong ReadUnsignedLittleEndian(ref SequenceReader<byte> reader, int byteCount, string field)
    {
        ulong value = 0;
        for (var index = 0; index < byteCount; index++)
            value |= (ulong)ReadByte(ref reader, field) << (index * 8);
        return value;
    }

    private static void Advance(ref SequenceReader<byte> reader, long count, string field)
    {
        if (count < 0 || reader.Remaining < count)
            throw new InvalidDataException($"Zstandard {field} is truncated.");
        reader.Advance(count);
    }
}
