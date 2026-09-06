using System.Linq;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class CompressionProviderTests
{
    [Test]
    public void TestProviderShouldRoundTripSingleAndMultiSegmentInput()
    {
        var provider = new TestCompressionProvider();
        var source = Enumerable.Repeat((byte)0x5a, 16 * 1024).ToArray();
        var segmented = CreateSegmented(source, 137);
        using var compressed = new PooledByteBufferWriter(source.Length);

        Ensure(provider.TryCompress(segmented, compressed, source.Length),
            "compressible candidate should fit");
        Ensure(compressed.WrittenCount < source.Length, "compressible payload should shrink");

        using var decompressed = new PooledByteBufferWriter(source.Length);
        provider.Decompress(
            CreateSegmented(compressed.WrittenMemory.ToArray(), 17),
            decompressed,
            source.Length);
        Ensure(decompressed.WrittenCount == source.Length, "decoded length");
        Ensure(decompressed.WrittenMemory.Span.SequenceEqual(source), "round-trip payload");
    }

    [Test]
    public void ProviderShouldUseTryCompressForBoundedCandidateFailure()
    {
        var provider = new TestCompressionProvider();
        var source = Enumerable.Repeat((byte)0x4a, 4096).ToArray();
        using var output = new PooledByteBufferWriter(source.Length);

        Ensure(!provider.TryCompress(
                new ReadOnlySequence<byte>(source),
                output,
                maxOutputBytes: 8),
            "bounded candidate failure is represented by false");
        Ensure(output.WrittenCount == 0,
            "the test provider rejects a bounded candidate before producing partial output");
    }

    [Test]
    [Arguments("truncated")]
    [Arguments("corrupt")]
    [Arguments("trailing")]
    public void ProviderShouldRejectMalformedCompletePayloads(string mutation)
    {
        var provider = new TestCompressionProvider();
        var source = Enumerable.Repeat((byte)0x33, 2048).ToArray();
        var compressed = Compress(provider, source).ToList();
        switch (mutation)
        {
            case "truncated":
                compressed.RemoveAt(compressed.Count - 1);
                break;
            case "corrupt":
                compressed[compressed.Count / 2] ^= 0x80;
                break;
            case "trailing":
                compressed.Add(0xff);
                break;
        }

        using var output = new PooledByteBufferWriter(source.Length);
        EnsureThrows<InvalidDataException>(() => provider.Decompress(
            new ReadOnlySequence<byte>(compressed.ToArray()),
            output,
            source.Length), mutation);
    }

    [Test]
    public void ProviderShouldRejectOutputBeyondBound()
    {
        var provider = new TestCompressionProvider();
        var source = Enumerable.Repeat((byte)0x22, 4096).ToArray();
        var compressed = Compress(provider, source);
        using var output = new PooledByteBufferWriter(source.Length);

        EnsureThrows<InvalidDataException>(() => provider.Decompress(
            new ReadOnlySequence<byte>(compressed),
            output,
            source.Length - 1), "decode output bound");
    }

    [Test]
    public void ProviderContractShouldBeSynchronousSmallAndAlgorithmNeutral()
    {
        var providerType = typeof(ISharpLinkCompressionProvider);
        Ensure(providerType.GetMethod(nameof(ISharpLinkCompressionProvider.TryCompress))?.ReturnType ==
            typeof(bool), "bounded compression is an explicit Try contract");
        Ensure(providerType.GetMethod(nameof(ISharpLinkCompressionProvider.Decompress))?.ReturnType ==
            typeof(void), "decompression success is represented by normal return");
        Ensure(providerType.GetProperty(nameof(ISharpLinkCompressionProvider.WireProfile))?.PropertyType ==
            typeof(string), "wire-profile negotiation contract");
        Ensure(providerType.GetProperty("Algorithm") is null,
            "provider contract should not expose an ambiguous algorithm name");
        Ensure(!providerType.GetMethods().Any(method => method.Name.EndsWith("Async", StringComparison.Ordinal)),
            "provider contract contains no asynchronous operation");

        var runtimeAssembly = typeof(ISharpLinkCompressionProvider).Assembly;
        Ensure(runtimeAssembly.GetType("SharpLink.Runtime.SharpLinkCompressionResult") is null,
            "Core should not expose duplicate consumed/written accounting");
        Ensure(runtimeAssembly.GetType("SharpLink.Runtime.SharpLinkCompressionProviders") is null,
            "Core should not ship a concrete compression provider factory");
        Ensure(runtimeAssembly.GetType("SharpLink.Runtime.BrotliCompressionProvider") is null,
            "Core should not contain a Brotli implementation");
    }

    [Test]
    public void CompressionOptionsShouldValidateTokensUniquenessAndBenefitThresholds()
    {
        var options = new SharpLinkCompressionOptions();
        options.Providers.Add(new TestCompressionProvider());
        options.Providers.Add(new TestCompressionProvider(maxRunLength: 64));
        EnsureThrows<ArgumentException>(options.Validate, "duplicate provider token");

        var invalid = new SharpLinkCompressionOptions();
        invalid.Providers.Add(new MutableTokenProvider("bad token"));
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

        var binding = context.Compression.ProviderBindings.Single();
        Ensure(binding.WireProfile == "test.mutable/v1" && ReferenceEquals(binding.Provider, provider),
            "runtime binding retains the profile validated during Build");
        Ensure(context.Compression.FindProviderBinding("test.mutable/v2") is null,
            "post-Build provider mutation must not change negotiation identity");
    }

    [Test]
    public void EmptyProviderListShouldRemainTheDefaultDisabledState()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build();
        Ensure(context.Compression.ProviderBindings.Count == 0,
            "Core ships with compression disabled and no concrete provider");
    }

    private static byte[] Compress(ISharpLinkCompressionProvider provider, byte[] source)
    {
        using var writer = new PooledByteBufferWriter(source.Length);
        Ensure(provider.TryCompress(
            new ReadOnlySequence<byte>(source),
            writer,
            source.Length), "test provider compression");
        return writer.WrittenMemory.ToArray();
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
            throw new InvalidOperationException($"Compression provider assertion failed: {scenario}.");
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

        public bool TryCompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
