using System.IO.Compression;
using System.Buffers.Binary;
using System.Linq;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class CompressionProviderTests
{
    [Test]
    public void BuiltInBrotliProviderShouldRoundTripSingleAndMultiSegmentInput()
    {
        var provider = SharpLinkCompressionProviders.CreateBrotli();
        Ensure(provider.WireProfile == "brotli", "built-in Brotli wire profile");
        var source = Enumerable.Repeat((byte)0x5a, 16 * 1024).ToArray();
        var segmented = CreateSegmented(source, 137);
        using var compressed = new PooledByteBufferWriter(source.Length);

        var compressedResult = provider.Compress(
            segmented, compressed, source.Length, CancellationToken.None);
        Ensure(compressedResult.ConsumedBytes == source.Length, "compress consumed bytes");
        Ensure(compressedResult.WrittenBytes == compressed.WrittenCount, "compress written bytes");
        Ensure(compressed.WrittenCount < source.Length, "compressible payload should shrink");

        using var decompressed = new PooledByteBufferWriter(source.Length);
        var compressedSegments = CreateSegmented(compressed.WrittenMemory.ToArray(), 17);
        var decompressedResult = provider.Decompress(
            compressedSegments, decompressed, source.Length, CancellationToken.None);
        Ensure(decompressedResult.ConsumedBytes == compressed.WrittenCount, "decompress consumed bytes");
        Ensure(decompressedResult.WrittenBytes == source.Length, "decompress written bytes");
        Ensure(decompressed.WrittenMemory.Span.SequenceEqual(source), "round-trip payload");
    }

    [Test]
    public void BuiltInBrotliProviderShouldRejectTruncatedOrTooSmallOutput()
    {
        var provider = SharpLinkCompressionProviders.CreateBrotli();
        var source = Enumerable.Repeat((byte)0x41, 4096).ToArray();
        using var compressed = new PooledByteBufferWriter(source.Length);
        provider.Compress(new ReadOnlySequence<byte>(source), compressed, source.Length);

        var truncatedBytes = compressed.WrittenMemory[..^1].ToArray();
        using var truncatedOutput = new PooledByteBufferWriter(source.Length);
        EnsureThrowsAny(
            () => provider.Decompress(
                new ReadOnlySequence<byte>(truncatedBytes),
                truncatedOutput,
                source.Length),
            "truncated compressed payload");

        using var boundedOutput = new PooledByteBufferWriter(source.Length);
        EnsureThrowsAny(
            () => provider.Decompress(
                new ReadOnlySequence<byte>(compressed.WrittenMemory),
                boundedOutput,
                source.Length - 1),
            "decompressed output limit");
    }

    [Test]
    public void BuiltInBrotliProviderShouldRejectTrailingDataWithARecomputedChecksum()
    {
        const int integrityTrailerBytes = sizeof(uint) + sizeof(uint);
        var provider = SharpLinkCompressionProviders.CreateBrotli();
        var source = Enumerable.Repeat((byte)0x52, 4096).ToArray();
        using var compressed = new PooledByteBufferWriter(source.Length);
        provider.Compress(new ReadOnlySequence<byte>(source), compressed, source.Length);
        var valid = compressed.WrittenMemory.ToArray();
        var compressedLength = valid.Length - integrityTrailerBytes;
        var mutated = new byte[valid.Length + 1];
        valid.AsSpan(0, compressedLength).CopyTo(mutated);
        mutated[compressedLength] = 0xff;
        valid.AsSpan(compressedLength).CopyTo(mutated.AsSpan(compressedLength + 1));
        var checksum = Crc32Accumulator.Compute(
            new ReadOnlySequence<byte>(mutated.AsMemory(0, mutated.Length - integrityTrailerBytes)));
        BinaryPrimitives.WriteUInt32LittleEndian(mutated.AsSpan(mutated.Length - sizeof(uint)), checksum);
        using var output = new PooledByteBufferWriter(source.Length);

        EnsureThrows<InvalidDataException>(
            () => provider.Decompress(
                new ReadOnlySequence<byte>(mutated),
                output,
                source.Length),
            "Brotli valid stream followed by trailing data");
    }

    [Test]
    public void BuiltInBrotliProviderShouldRoundTripVariedPayloadsAndCompressionLevels()
    {
        CompressionLevel[] levels =
        [
            CompressionLevel.NoCompression,
            CompressionLevel.Fastest,
            CompressionLevel.Optimal,
            CompressionLevel.SmallestSize
        ];
        int[] lengths = [1, 2, 31, 256, 4096];
        foreach (var level in levels)
        {
            foreach (var length in lengths)
            {
                var source = new byte[length];
                new Random(length + 17).NextBytes(source);
                RoundTrip(SharpLinkCompressionProviders.CreateBrotli(level), source, $"brotli/{level}/random/{length}");
                Array.Fill(source, (byte)0x3c);
                RoundTrip(SharpLinkCompressionProviders.CreateBrotli(level), source, $"brotli/{level}/repeat/{length}");
            }
        }
    }

    [Test]
    public void CompressionProviderContractShouldBeExplicitlySynchronous()
    {
        var providerType = typeof(ISharpLinkCompressionProvider);
        Ensure(providerType.GetMethod(nameof(ISharpLinkCompressionProvider.Compress))?.ReturnType ==
            typeof(SharpLinkCompressionResult), "synchronous compression contract");
        Ensure(providerType.GetMethod(nameof(ISharpLinkCompressionProvider.Decompress))?.ReturnType ==
            typeof(SharpLinkCompressionResult), "synchronous decompression contract");
        Ensure(providerType.GetProperty(nameof(ISharpLinkCompressionProvider.WireProfile))?.PropertyType ==
            typeof(string), "wire-profile negotiation contract");
        Ensure(providerType.GetProperty("Algorithm") is null, "provider contract should not expose an ambiguous algorithm name");
        Ensure(!providerType.GetMethods().Any(method => method.Name.EndsWith("Async", StringComparison.Ordinal)),
            "provider contract contains no asynchronous operation");
    }

    [Test]
    public void BuiltInFactoryShouldOnlyExposeBrotli()
    {
        var factories = typeof(SharpLinkCompressionProviders)
            .GetMethods()
            .Where(method => method.IsPublic && method.IsStatic &&
                method.ReturnType == typeof(ISharpLinkCompressionProvider))
            .Select(method => method.Name)
            .ToArray();
        Ensure(factories.SequenceEqual([nameof(SharpLinkCompressionProviders.CreateBrotli)]),
            "only Brotli should be exposed as a built-in provider");
    }

    [Test]
    public void CompressionOptionsShouldValidateTokensUniquenessAndBenefitThresholds()
    {
        var options = new SharpLinkCompressionOptions();
        options.Providers.Add(SharpLinkCompressionProviders.CreateBrotli());
        options.Providers.Add(SharpLinkCompressionProviders.CreateBrotli(CompressionLevel.Optimal));
        EnsureThrows<ArgumentException>(options.Validate, "duplicate provider token");

        var invalid = new SharpLinkCompressionOptions();
        invalid.Providers.Add(new InvalidTokenProvider("bad token"));
        EnsureThrows<ArgumentException>(invalid.Validate, "non-canonical provider token");

        var ratio = new SharpLinkCompressionOptions { MinimumSavingsRatio = 1.01 };
        EnsureThrows<ArgumentOutOfRangeException>(ratio.Validate, "invalid savings ratio");
    }

    private static void RoundTrip(
        ISharpLinkCompressionProvider provider,
        byte[] source,
        string scenario)
    {
        using var compressed = new PooledByteBufferWriter(Math.Max(1, source.Length * 2 + 1024));
        provider.Compress(
            new ReadOnlySequence<byte>(source),
            compressed,
            source.Length * 2 + 1024);
        using var decompressed = new PooledByteBufferWriter(Math.Max(1, source.Length));
        SharpLinkCompressionResult result;
        try
        {
            result = provider.Decompress(
                new ReadOnlySequence<byte>(compressed.WrittenMemory),
                decompressed,
                source.Length);
        }
        catch (Exception exception)
        {
            throw new InvalidDataException($"Round-trip failed for {scenario}.", exception);
        }
        Ensure(result.WrittenBytes == source.Length, $"{scenario} decoded length");
        Ensure(decompressed.WrittenMemory.Span.SequenceEqual(source), $"{scenario} payload");
    }

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

    private static void EnsureThrowsAny(Action action, string scenario)
    {
        try
        {
            action();
        }
        catch (Exception) when (scenario.Length != 0)
        {
            return;
        }
        throw new InvalidOperationException($"Expected provider failure: {scenario}.");
    }

    private sealed class InvalidTokenProvider(string wireProfile) : ISharpLinkCompressionProvider
    {
        public string WireProfile { get; } = wireProfile;
        public SharpLinkCompressionResult Compress(
            ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public SharpLinkCompressionResult Decompress(
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
