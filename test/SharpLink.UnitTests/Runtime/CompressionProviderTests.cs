using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class CompressionProviderTests
{
    [Test]
    [Arguments("gzip")]
    [Arguments("deflate")]
    [Arguments("brotli")]
    public async Task BuiltInProviderShouldRoundTripSingleAndMultiSegmentInput(string algorithm)
    {
        var provider = CreateProvider(algorithm);
        var source = Enumerable.Repeat((byte)0x5a, 16 * 1024).ToArray();
        var segmented = CreateSegmented(source, 137);
        using var compressed = new PooledByteBufferWriter(source.Length);

        var compressedResult = await provider.CompressAsync(
            segmented, compressed, source.Length, CancellationToken.None);
        Ensure(compressedResult.ConsumedBytes == source.Length, "compress consumed bytes");
        Ensure(compressedResult.WrittenBytes == compressed.WrittenCount, "compress written bytes");
        Ensure(compressed.WrittenCount < source.Length, "compressible payload should shrink");

        using var decompressed = new PooledByteBufferWriter(source.Length);
        var compressedSegments = CreateSegmented(compressed.WrittenMemory.ToArray(), 17);
        var decompressedResult = await provider.DecompressAsync(
            compressedSegments, decompressed, source.Length, CancellationToken.None);
        Ensure(decompressedResult.ConsumedBytes == compressed.WrittenCount, "decompress consumed bytes");
        Ensure(decompressedResult.WrittenBytes == source.Length, "decompress written bytes");
        Ensure(decompressed.WrittenMemory.Span.SequenceEqual(source), "round-trip payload");
    }

    [Test]
    [Arguments("gzip")]
    [Arguments("deflate")]
    [Arguments("brotli")]
    public async Task BuiltInProviderShouldRejectTruncatedOrTooSmallOutput(string algorithm)
    {
        var provider = CreateProvider(algorithm);
        var source = Enumerable.Repeat((byte)0x41, 4096).ToArray();
        using var compressed = new PooledByteBufferWriter(source.Length);
        await provider.CompressAsync(new ReadOnlySequence<byte>(source), compressed, source.Length);

        var truncatedBytes = compressed.WrittenMemory[..^1].ToArray();
        using var truncatedOutput = new PooledByteBufferWriter(source.Length);
        await EnsureThrowsAnyAsync(
            () => provider.DecompressAsync(
                new ReadOnlySequence<byte>(truncatedBytes),
                truncatedOutput,
                source.Length).AsTask(),
            "truncated compressed payload");

        using var boundedOutput = new PooledByteBufferWriter(source.Length);
        await EnsureThrowsAnyAsync(
            () => provider.DecompressAsync(
                new ReadOnlySequence<byte>(compressed.WrittenMemory),
                boundedOutput,
                source.Length - 1).AsTask(),
            "decompressed output limit");
    }

    [Test]
    public void CompressionOptionsShouldValidateTokensUniquenessAndBenefitThresholds()
    {
        var options = new SharpLinkCompressionOptions();
        options.Providers.Add(SharpLinkCompressionProviders.CreateGzip());
        options.Providers.Add(SharpLinkCompressionProviders.CreateGzip(CompressionLevel.Optimal));
        EnsureThrows<ArgumentException>(options.Validate, "duplicate provider token");

        var invalid = new SharpLinkCompressionOptions();
        invalid.Providers.Add(new InvalidTokenProvider("bad token"));
        EnsureThrows<ArgumentException>(invalid.Validate, "non-canonical provider token");

        var ratio = new SharpLinkCompressionOptions { MinimumSavingsRatio = 1.01 };
        EnsureThrows<ArgumentOutOfRangeException>(ratio.Validate, "invalid savings ratio");
    }

    private static ISharpLinkCompressionProvider CreateProvider(string algorithm)
        => algorithm switch
        {
            "gzip" => SharpLinkCompressionProviders.CreateGzip(),
            "deflate" => SharpLinkCompressionProviders.CreateDeflate(),
            "brotli" => SharpLinkCompressionProviders.CreateBrotli(),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
        };

    private static ReadOnlySequence<byte> CreateSegmented(byte[] bytes, int segmentSize)
    {
        BufferSegment? first = null;
        BufferSegment? last = null;
        for (var offset = 0; offset < bytes.Length; offset += segmentSize)
        {
            var segment = new BufferSegment(bytes.AsMemory(offset, Math.Min(segmentSize, bytes.Length - offset)));
            if (first is null)
                first = segment;
            else
                last!.SetNext(segment);
            last = segment;
        }
        return first is null
            ? ReadOnlySequence<byte>.Empty
            : new ReadOnlySequence<byte>(first, 0, last!, last!.Memory.Length);
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new InvalidOperationException($"Compression assertion failed: {scenario}.");
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

    private static async Task EnsureThrowsAnyAsync(Func<Task> action, string scenario)
    {
        try
        {
            await action();
        }
        catch (Exception) when (scenario.Length != 0)
        {
            return;
        }
        throw new InvalidOperationException($"Expected provider failure: {scenario}.");
    }

    private sealed class InvalidTokenProvider(string algorithm) : ISharpLinkCompressionProvider
    {
        public string Algorithm { get; } = algorithm;
        public ValueTask<SharpLinkCompressionResult> CompressAsync(
            ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SharpLinkCompressionResult> DecompressAsync(
            ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public void SetNext(BufferSegment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
        }
    }
}
