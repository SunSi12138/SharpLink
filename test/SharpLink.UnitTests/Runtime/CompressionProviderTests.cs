using System.IO.Compression;
using System.Buffers.Binary;
using System.Linq;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class CompressionProviderTests
{
    private const uint IntegrityMagic = 0x31504353; // "SCP1" in little endian.
    private const int IntegrityTrailerBytes = sizeof(uint) + sizeof(uint);

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
    public void BuiltInBrotliProviderShouldRoundTripSegmentedCompressedBoundaryShapes()
    {
        var provider = SharpLinkCompressionProviders.CreateBrotli();
        var source = CreateDeterministicPayload(1024);
        var compressed = CompressPayload(provider, source);
        var compressedBodyLength = compressed.Length - IntegrityTrailerBytes;
        Ensure(compressedBodyLength > 2, "compressed body has boundary test bytes");

        VerifyRoundTrip(provider, source,
            CreateSegmentedAtOffsets(compressed, compressed.Length / 2),
            "two compressed segments");
        VerifyRoundTrip(provider, source,
            CreateSegmentedByCount(compressed, 8),
            "eight compressed segments");
        VerifyRoundTrip(provider, source,
            CreateSegmented(compressed, 1),
            "one-byte compressed segments");
        VerifyRoundTrip(provider, source,
            CreateSegmentedAtOffsets(compressed, compressedBodyLength - 1, compressedBodyLength),
            "last compressed body byte in its own segment");
        VerifyRoundTrip(provider, source,
            CreateSegmentedAtOffsets(compressed,
                Enumerable.Range(compressedBodyLength, IntegrityTrailerBytes - 1).ToArray()),
            "each integrity trailer byte in its own segment");
        VerifyRoundTrip(provider, source,
            CreateSegmentedAtOffsets(compressed, compressedBodyLength),
            "body and trailer at a segment boundary");
        VerifyRoundTrip(provider, source,
            CreateSegmentedAtOffsets(compressed, compressedBodyLength - 1, compressedBodyLength + 2),
            "body and trailer boundary inside a segment");
    }

    [Test]
    public void BuiltInBrotliProviderShouldDecodeBrotliTokensAcrossEveryBodySplit()
    {
        var provider = SharpLinkCompressionProviders.CreateBrotli();
        var source = CreateDeterministicPayload(512);
        var compressed = CompressPayload(provider, source);
        var compressedBodyLength = compressed.Length - IntegrityTrailerBytes;

        for (var splitOffset = 1; splitOffset < compressedBodyLength; splitOffset++)
        {
            VerifyRoundTrip(provider, source,
                CreateSegmentedAtOffsets(compressed, splitOffset),
                $"compressed body split at {splitOffset}");
        }
    }

    [Test]
    public void BuiltInBrotliProviderShouldPreserveSegmentedIntegrityAndOutputLimitChecks()
    {
        var provider = SharpLinkCompressionProviders.CreateBrotli();
        var source = CreateDeterministicPayload(2048);
        var compressed = CompressPayload(provider, source);
        var compressedBodyLength = compressed.Length - IntegrityTrailerBytes;

        var truncatedBody = compressed.AsSpan(0, compressedBodyLength - 1).ToArray();
        EnsureDecompressionThrows<InvalidDataException>(
            provider,
            CreateSegmented(AppendIntegrityTrailer(truncatedBody), 3),
            source.Length,
            "compressed body missing its final byte");

        EnsureDecompressionThrows<InvalidDataException>(
            provider,
            CreateSegmented(compressed[..^1], 1),
            source.Length,
            "integrity trailer missing one byte");

        var missingMagic = compressed.ToArray();
        missingMagic[compressedBodyLength] ^= 0x01;
        EnsureDecompressionThrows<InvalidDataException>(
            provider,
            CreateSegmented(missingMagic, 5),
            source.Length,
            "integrity magic corruption");

        var checksumCorruption = compressed.ToArray();
        checksumCorruption[compressedBodyLength - 1] ^= 0x80;
        EnsureDecompressionThrows<InvalidDataException>(
            provider,
            CreateSegmented(checksumCorruption, 7),
            source.Length,
            "integrity checksum corruption");

        var bodyWithTrailingByte = new byte[compressedBodyLength + 1];
        compressed.AsSpan(0, compressedBodyLength).CopyTo(bodyWithTrailingByte);
        bodyWithTrailingByte[^1] = 0xff;
        EnsureDecompressionThrows<InvalidDataException>(
            provider,
            CreateSegmentedAtOffsets(
                AppendIntegrityTrailer(bodyWithTrailingByte),
                compressedBodyLength - 1,
                compressedBodyLength + 1),
            source.Length,
            "valid Brotli stream followed by trailing data");

        VerifyRoundTrip(provider, source,
            CreateSegmented(compressed, 11),
            "exact decompressed output limit");
        EnsureDecompressionThrows<SharpLinkCompressionOutputLimitException>(
            provider,
            CreateSegmented(compressed, 11),
            source.Length - 1,
            "decompressed output limit one byte below exact length");
    }

    [Test]
    public void BuiltInBrotliProviderShouldObserveCancellationDuringSegmentedDecode()
    {
        var provider = SharpLinkCompressionProviders.CreateBrotli();
        var source = CreateDeterministicPayload(64 * 1024);
        var compressed = CompressPayload(provider, source);
        var segmented = CreateSegmented(compressed, 257);

        VerifyRoundTrip(provider, source, segmented, "large segmented decode");

        using var cancelledBeforeDecode = new CancellationTokenSource();
        cancelledBeforeDecode.Cancel();
        using var cancelledBeforeDecodeOutput = new PooledByteBufferWriter(source.Length);
        EnsureThrows<OperationCanceledException>(
            () => provider.Decompress(
                segmented,
                cancelledBeforeDecodeOutput,
                source.Length,
                cancelledBeforeDecode.Token),
            "cancellation before segmented decode");
        Ensure(cancelledBeforeDecodeOutput.WrittenCount == 0,
            "cancellation before decode must not write output");

        using var cancelledDuringDecode = new CancellationTokenSource();
        using var cancelledDuringDecodeOutput = new PooledByteBufferWriter(source.Length);
        var cancellingWriter = new CancelAfterFirstAdvanceBufferWriter(
            cancelledDuringDecodeOutput,
            cancelledDuringDecode);
        EnsureThrows<OperationCanceledException>(
            () => provider.Decompress(
                segmented,
                cancellingWriter,
                source.Length,
                cancelledDuringDecode.Token),
            "cancellation between segmented decoder calls");
        Ensure(cancelledDuringDecode.IsCancellationRequested,
            "test writer should cancel after decoded output is produced");
        Ensure(cancelledDuringDecodeOutput.WrittenCount > 0,
            "cancellation during decode must happen after the first output chunk");
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
        var provider = SharpLinkCompressionProviders.CreateBrotli();
        var source = Enumerable.Repeat((byte)0x52, 4096).ToArray();
        using var compressed = new PooledByteBufferWriter(source.Length);
        provider.Compress(new ReadOnlySequence<byte>(source), compressed, source.Length);
        var valid = compressed.WrittenMemory.ToArray();
        var compressedLength = valid.Length - IntegrityTrailerBytes;
        var mutated = new byte[valid.Length + 1];
        valid.AsSpan(0, compressedLength).CopyTo(mutated);
        mutated[compressedLength] = 0xff;
        valid.AsSpan(compressedLength).CopyTo(mutated.AsSpan(compressedLength + 1));
        var checksum = Crc32Accumulator.Compute(
            new ReadOnlySequence<byte>(mutated.AsMemory(0, mutated.Length - IntegrityTrailerBytes)));
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

    [Test]
    public void RuntimeSnapshotShouldFreezeAProvidersValidatedWireProfile()
    {
        var provider = new MutableTokenProvider("test.mutable/v1");
        using var context = new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.Compression.Providers.Add(provider))
            .Build();

        Ensure(provider.ProfileReads == 1,
            "Runtime Build must validate a provider's wire identity exactly once");
        provider.WireProfile = "test.mutable/v2";

        Ensure(ReferenceEquals(
                context.Compression.FindProvider("test.mutable/v1"), provider),
            "runtime lookup must retain the profile validated during Build");
        Ensure(context.Compression.FindProvider("test.mutable/v2") is null,
            "post-Build provider mutation must not change negotiation identity");
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

    private static byte[] CompressPayload(
        ISharpLinkCompressionProvider provider,
        byte[] source)
    {
        var maxCompressedBytes = checked(source.Length * 2 + 1024);
        using var compressed = new PooledByteBufferWriter(maxCompressedBytes);
        provider.Compress(
            new ReadOnlySequence<byte>(source),
            compressed,
            maxCompressedBytes);
        return compressed.WrittenMemory.ToArray();
    }

    private static void VerifyRoundTrip(
        ISharpLinkCompressionProvider provider,
        byte[] source,
        ReadOnlySequence<byte> input,
        string scenario)
    {
        using var output = new PooledByteBufferWriter(source.Length);
        var result = provider.Decompress(input, output, source.Length);
        Ensure(result.ConsumedBytes == input.Length, $"{scenario} consumed bytes");
        Ensure(result.WrittenBytes == source.Length, $"{scenario} written bytes");
        Ensure(output.WrittenMemory.Span.SequenceEqual(source), $"{scenario} payload");
    }

    private static TException EnsureDecompressionThrows<TException>(
        ISharpLinkCompressionProvider provider,
        ReadOnlySequence<byte> input,
        int maxOutputBytes,
        string scenario)
        where TException : Exception
    {
        using var output = new PooledByteBufferWriter(Math.Max(1, Math.Min(maxOutputBytes, 8192)));
        return EnsureThrows<TException>(
            () => provider.Decompress(input, output, maxOutputBytes),
            scenario);
    }

    private static byte[] AppendIntegrityTrailer(byte[] compressedBody)
    {
        var payload = new byte[compressedBody.Length + IntegrityTrailerBytes];
        compressedBody.CopyTo(payload, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(compressedBody.Length), IntegrityMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(compressedBody.Length + sizeof(uint)),
            Crc32Accumulator.Compute(new ReadOnlySequence<byte>(compressedBody)));
        return payload;
    }

    private static byte[] CreateDeterministicPayload(int length)
    {
        var payload = new byte[length];
        new Random(length + 1979).NextBytes(payload);
        return payload;
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

    private static ReadOnlySequence<byte> CreateSegmentedByCount(byte[] bytes, int segmentCount)
    {
        if (segmentCount is <= 0 or > bytes.Length)
            throw new ArgumentOutOfRangeException(nameof(segmentCount));

        var offsets = new int[segmentCount - 1];
        for (var segment = 1; segment < segmentCount; segment++)
            offsets[segment - 1] = checked((int)((long)bytes.Length * segment / segmentCount));
        return CreateSegmentedAtOffsets(bytes, offsets);
    }

    private static ReadOnlySequence<byte> CreateSegmentedAtOffsets(byte[] bytes, params int[] offsets)
    {
        BufferSegment? first = null;
        BufferSegment? last = null;
        var offset = 0;
        foreach (var nextOffset in offsets)
        {
            if (nextOffset <= offset || nextOffset >= bytes.Length)
                throw new ArgumentOutOfRangeException(nameof(offsets));
            var segment = new BufferSegment(bytes.AsMemory(offset, nextOffset - offset));
            if (first is null)
                first = segment;
            else
                last!.SetNext(segment);
            last = segment;
            offset = nextOffset;
        }

        var finalSegment = new BufferSegment(bytes.AsMemory(offset));
        if (first is null)
            first = finalSegment;
        else
            last!.SetNext(finalSegment);
        last = finalSegment;
        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new InvalidOperationException($"Compression assertion failed: {scenario}.");
    }

    private static TException EnsureThrows<TException>(Action action, string scenario)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
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

    private sealed class MutableTokenProvider(string wireProfile) : ISharpLinkCompressionProvider
    {
        private string _wireProfile = wireProfile;

        public int ProfileReads { get; private set; }

        public string WireProfile
        {
            get
            {
                ProfileReads++;
                return _wireProfile;
            }
            set => _wireProfile = value;
        }

        public SharpLinkCompressionResult Compress(
            ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public SharpLinkCompressionResult Decompress(
            ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CancelAfterFirstAdvanceBufferWriter(
        IBufferWriter<byte> inner,
        CancellationTokenSource cancellation) : IBufferWriter<byte>
    {
        private bool _cancelled;

        public void Advance(int count)
        {
            inner.Advance(count);
            if (count != 0 && !_cancelled)
            {
                _cancelled = true;
                cancellation.Cancel();
            }
        }

        public Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);

        public Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
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
