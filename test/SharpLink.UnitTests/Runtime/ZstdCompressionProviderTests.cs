using System.Linq;
using SharpLink.Compression.Zstd;

namespace SharpLink.UnitTests.Runtime;

public class ZstdCompressionProviderTests
{
    [Test]
    public void ProviderShouldRoundTripContiguousAndSegmentedPayloads()
    {
        var provider = new SharpLinkZstdCompressionProvider();
        var source = CreateDtoLikePayload(64 * 1024);
        var compressed = Compress(provider, CreateSegmented(source, 997), source.Length);

        Ensure(compressed.Length < source.Length, "DTO-like payload should compress");
        using var output = new PooledByteBufferWriter(source.Length);
        provider.Decompress(
            CreateSegmented(compressed, 113),
            output,
            source.Length);

        Ensure(output.WrittenCount == source.Length, "decoded byte count");
        Ensure(output.WrittenMemory.Span.SequenceEqual(source), "segmented Zstd round-trip");
    }

    [Test]
    [Arguments("truncated")]
    [Arguments("checksum")]
    [Arguments("trailing")]
    [Arguments("concatenated")]
    [Arguments("checksum-disabled")]
    [Arguments("dictionary")]
    public void ProviderShouldRejectRepresentationsOutsideTheWireProfile(string mutation)
    {
        var provider = new SharpLinkZstdCompressionProvider();
        var source = CreateDtoLikePayload(16 * 1024);
        var compressed = Compress(provider, new ReadOnlySequence<byte>(source), source.Length).ToList();

        switch (mutation)
        {
            case "truncated":
                compressed.RemoveAt(compressed.Count - 1);
                break;
            case "checksum":
                compressed[^1] ^= 0x40;
                break;
            case "trailing":
                compressed.Add(0x00);
                break;
            case "concatenated":
                compressed.AddRange(compressed.ToArray());
                break;
            case "checksum-disabled":
                compressed[4] &= unchecked((byte)~0x04);
                break;
            case "dictionary":
                compressed[4] = (byte)((compressed[4] & ~0x03) | 0x01);
                break;
        }

        using var output = new PooledByteBufferWriter(source.Length);
        EnsureThrows<InvalidDataException>(() => provider.Decompress(
            CreateSegmented(compressed.ToArray(), 79),
            output,
            source.Length), mutation);
    }

    [Test]
    public void ProviderShouldHonorCompressionAndDecompressionBounds()
    {
        var provider = new SharpLinkZstdCompressionProvider();
        var source = CreateDtoLikePayload(4096);
        using var tooSmall = new PooledByteBufferWriter(source.Length);
        Ensure(!provider.TryCompress(
            new ReadOnlySequence<byte>(source),
            tooSmall,
            maxOutputBytes: 8), "bounded compression candidate should return false");

        var compressed = Compress(provider, new ReadOnlySequence<byte>(source), source.Length);
        using var output = new PooledByteBufferWriter(source.Length);
        EnsureThrows<InvalidDataException>(() => provider.Decompress(
            new ReadOnlySequence<byte>(compressed),
            output,
            source.Length - 1), "decompression bound");
    }

    [Test]
    public void EncodeTuningShouldNotChangeWireIdentity()
    {
        var fast = new SharpLinkZstdCompressionProvider(1);
        var normal = new SharpLinkZstdCompressionProvider(3);
        var stronger = new SharpLinkZstdCompressionProvider(7);

        Ensure(fast.WireProfile == SharpLinkZstdCompressionProvider.Profile, "fast profile");
        Ensure(normal.WireProfile == fast.WireProfile, "normal profile identity");
        Ensure(stronger.WireProfile == fast.WireProfile, "stronger profile identity");
    }

    [Test]
    public void ProviderShouldObservePreCancelledOperations()
    {
        var provider = new SharpLinkZstdCompressionProvider();
        var source = CreateDtoLikePayload(4096);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var output = new PooledByteBufferWriter(source.Length);

        EnsureThrows<OperationCanceledException>(() => provider.TryCompress(
            new ReadOnlySequence<byte>(source),
            output,
            source.Length,
            cancellation.Token), "compression cancellation");

        var compressed = Compress(provider, new ReadOnlySequence<byte>(source), source.Length);
        using var decoded = new PooledByteBufferWriter(source.Length);
        EnsureThrows<OperationCanceledException>(() => provider.Decompress(
            new ReadOnlySequence<byte>(compressed),
            decoded,
            source.Length,
            cancellation.Token), "decompression cancellation");
    }

    [Test]
    public void ProviderInstanceShouldSupportConcurrentCalls()
    {
        var provider = new SharpLinkZstdCompressionProvider();
        var source = CreateDtoLikePayload(32 * 1024);
        Parallel.For(0, 32, _ =>
        {
            var compressed = Compress(provider, new ReadOnlySequence<byte>(source), source.Length);
            using var output = new PooledByteBufferWriter(source.Length);
            provider.Decompress(new ReadOnlySequence<byte>(compressed), output, source.Length);
            Ensure(output.WrittenMemory.Span.SequenceEqual(source), "parallel round-trip");
        });
    }

    private static byte[] Compress(
        ISharpLinkCompressionProvider provider,
        ReadOnlySequence<byte> input,
        int maxOutputBytes)
    {
        using var writer = new PooledByteBufferWriter(maxOutputBytes);
        Ensure(provider.TryCompress(input, writer, maxOutputBytes), "Zstd compression should fit");
        return writer.WrittenMemory.ToArray();
    }

    private static byte[] CreateDtoLikePayload(int length)
    {
        var payload = new byte[length];
        var token = "{\"id\":12345,\"name\":\"SharpLink\",\"region\":\"ap-northeast-1\",\"enabled\":true}"u8;
        for (var offset = 0; offset < payload.Length; offset += token.Length)
            token[..Math.Min(token.Length, payload.Length - offset)].CopyTo(payload.AsSpan(offset));
        return payload;
    }

    private static ReadOnlySequence<byte> CreateSegmented(byte[] bytes, int segmentSize)
    {
        Segment? first = null;
        Segment? last = null;
        for (var offset = 0; offset < bytes.Length; offset += segmentSize)
        {
            var segment = new Segment(bytes.AsMemory(offset, Math.Min(segmentSize, bytes.Length - offset)));
            if (first is null)
                first = segment;
            else
                last!.SetNext(segment);
            last = segment;
        }
        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    private static void EnsureThrows<TException>(Action action, string scenario)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}: {scenario}.");
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new InvalidOperationException($"Zstd compression assertion failed: {scenario}.");
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment(ReadOnlyMemory<byte> memory) => Memory = memory;
        internal void SetNext(Segment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
        }
    }
}
