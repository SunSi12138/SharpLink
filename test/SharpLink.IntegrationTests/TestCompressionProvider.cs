using System.Buffers.Binary;

namespace SharpLink.IntegrationTests;

internal sealed class TestCompressionProvider(
    string wireProfile = "test.rle/v1",
    int maxRunLength = byte.MaxValue) : ISharpLinkCompressionProvider
{
    private const uint Magic = 0x31524C54; // "TLR1" little endian.
    private const int FixedBytes = sizeof(uint) + sizeof(uint);

    public string WireProfile { get; } = wireProfile;

    public bool TryCompress(
        ReadOnlySequence<byte> input,
        IBufferWriter<byte> output,
        int maxOutputBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegative(maxOutputBytes);
        if (maxRunLength is < 1 or > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maxRunLength));

        var source = input.ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var runCount = CountRuns(source, maxRunLength, cancellationToken);
        var required = checked(FixedBytes + runCount * 2);
        if (required > maxOutputBytes)
            return false;

        Span<byte> header = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        output.Write(header);
        var checksum = 2166136261u;
        Span<byte> run = stackalloc byte[2];
        for (var offset = 0; offset < source.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = source[offset];
            var count = 1;
            checksum = AppendChecksum(checksum, value);
            while (offset + count < source.Length && count < maxRunLength && source[offset + count] == value)
            {
                checksum = AppendChecksum(checksum, value);
                count++;
            }
            run[0] = checked((byte)count);
            run[1] = value;
            output.Write(run);
            offset += count;
        }
        Span<byte> trailer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, checksum);
        output.Write(trailer);
        return true;
    }

    public void Decompress(
        ReadOnlySequence<byte> input,
        IBufferWriter<byte> output,
        int maxOutputBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegative(maxOutputBytes);
        var source = input.ToArray();
        if (source.Length < FixedBytes || (source.Length - FixedBytes) % 2 != 0)
            throw new InvalidDataException("Test compression payload is truncated or has trailing data.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic)
            throw new InvalidDataException("Test compression payload magic is invalid.");

        var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(source.Length - sizeof(uint)));
        var checksum = 2166136261u;
        var written = 0;
        for (var offset = sizeof(uint); offset < source.Length - sizeof(uint); offset += 2)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = source[offset];
            var value = source[offset + 1];
            if (count == 0 || count > maxOutputBytes - written)
                throw new InvalidDataException("Test compression payload exceeds its output limit.");
            var span = output.GetSpan(count)[..count];
            span.Fill(value);
            output.Advance(count);
            written += count;
            for (var index = 0; index < count; index++)
                checksum = AppendChecksum(checksum, value);
        }
        if (checksum != expectedChecksum)
            throw new InvalidDataException("Test compression payload checksum is invalid.");
    }

    private static int CountRuns(byte[] source, int runLimit, CancellationToken cancellationToken)
    {
        var runs = 0;
        for (var offset = 0; offset < source.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = source[offset];
            var count = 1;
            while (offset + count < source.Length && count < runLimit && source[offset + count] == value)
                count++;
            runs++;
            offset += count;
        }
        return runs;
    }

    private static uint AppendChecksum(uint checksum, byte value)
        => (checksum ^ value) * 16777619u;
}

internal sealed class RejectingCompressionProvider : ISharpLinkCompressionProvider
{
    public string WireProfile => "test.reject/v1";

    public bool TryCompress(
        ReadOnlySequence<byte> input,
        IBufferWriter<byte> output,
        int maxOutputBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    public void Decompress(
        ReadOnlySequence<byte> input,
        IBufferWriter<byte> output,
        int maxOutputBytes,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Rejected candidates must never reach a decoder.");
}
