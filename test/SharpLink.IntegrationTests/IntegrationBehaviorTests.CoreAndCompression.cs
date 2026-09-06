namespace SharpLink.IntegrationTests;

public partial class IntegrationBehaviorTests
{
    [Test]
    public void GeneratedBooleanMemberShouldRejectNonCanonicalPayload()
    {
        var failure = DeserializeMutatedGeneratedSemantic(1, static (payload, offset, _) => payload[offset] = 2);

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DataLoss },
            "generated Boolean member must reject a marker other than zero or one");
    }

    [Test]
    public void GeneratedRuneMemberShouldRejectInvalidScalar()
    {
        var failure = DeserializeMutatedGeneratedSemantic(2, static (payload, offset, _) =>
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset), 0x11_0000));

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DataLoss },
            "generated Rune member must reject a scalar above the Unicode maximum");
    }

    [Test]
    public void GeneratedDecimalMemberShouldRejectInvalidLayout()
    {
        var failure = DeserializeMutatedGeneratedSemantic(3, static (payload, offset, length) =>
            payload.AsSpan(offset, length).Fill(0xFF));

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DataLoss },
            "generated decimal member must reject an invalid flags layout");
    }

    [Test]
    public void GeneratedTemporalMembersShouldRejectInvalidValues()
    {
        var dateOnlyFailure = DeserializeMutatedGeneratedSemantic(4, static (payload, offset, _) =>
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset), int.MaxValue));
        var dateTimeFailure = DeserializeMutatedGeneratedSemantic(5, static (payload, offset, _) =>
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset), DateTime.MaxValue.Ticks + 1));
        var timeOnlyFailure = DeserializeMutatedGeneratedSemantic(6, static (payload, offset, _) =>
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset), long.MaxValue));

        Ensure(dateOnlyFailure is SharpLinkException { Code: SharpLinkErrorCode.DataLoss } &&
               dateTimeFailure is SharpLinkException { Code: SharpLinkErrorCode.DataLoss } &&
               timeOnlyFailure is SharpLinkException { Code: SharpLinkErrorCode.DataLoss },
            "generated DateOnly, DateTime, and TimeOnly members must reject invalid values");
    }

    [Test]
    public void GeneratedDateTimeOffsetMemberShouldUseCanonicalValidatedPayload()
    {
        var serialized = SerializeGeneratedSemantic();
        var field = FindGeneratedSemanticField(serialized, 7);
        var malformedFailure = DeserializeMutatedGeneratedSemantic(7, static (payload, offset, _) =>
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset + sizeof(long)), long.MaxValue));

        var paddingFailure = DeserializeMutatedGeneratedSemantic(7, static (payload, offset, _) =>
            payload[offset + sizeof(short)] = 0xA5);

        var paddingIsCanonical = field.WireType == RpcGeneratedWireType.Fixed16 && field.Length == 16 &&
                                 field.Offset + field.Length <= serialized.Length &&
                                 serialized.AsSpan(field.Offset + sizeof(short), 6).IndexOfAnyExcept((byte)0) < 0;
        Ensure(paddingIsCanonical &&
               malformedFailure is SharpLinkException { Code: SharpLinkErrorCode.DataLoss } &&
               paddingFailure is SharpLinkException { Code: SharpLinkErrorCode.DataLoss },
            "generated DateTimeOffset must emit canonical padding and reject malformed ticks or padding");
    }

    [Test]
    public void GeneratedNullCollectionShouldRejectTrailingBytes()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build();
        var codec = context.Codecs.GetCodec<List<string>>();
        var failure = CaptureException(() => codec.Deserialize(
            new ReadOnlySequence<byte>(new byte[] { 0, 0xA5 })));

        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DataLoss },
            "null generated collection must reject trailing bytes");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BasicRpcAndStreamingShouldWork(bool useSharedMemory)
    {
        static IRpcCodec? Resolver(Type type)
        {
            if (type == typeof(GeneratedEnvelope) ||
                type == typeof(GeneratedAddress) ||
                type == typeof(List<string>))
            {
                throw new Exception($"Generated Codec unexpectedly fell through to resolver: {type}.");
            }
            return null;
        }

        await using var harness = await TestHarness.CreateAsync(
            codecResolver: Resolver,
            useSharedMemory: useSharedMemory);
        var svc = harness.Client.Get<ITestService>();

        var add = await svc.AddAsync(10, 20);
        Ensure(add == 30, "AddAsync");

        var echo = await svc.EchoAsync(new Person { Name = "s", Age = 1, Tags = ["x"] });
        Ensure(echo is { Name: "s-r", Age: 2 }, "EchoAsync");

        var generated = await svc.EchoGeneratedAsync(new GeneratedEnvelope(
            "native",
            7,
            new GeneratedAddress("Shanghai"),
            ["rpc", "aot"]));
        Ensure(generated is
        {
            Name: "native-r",
            Age: 8,
            Address.City: "Shanghai",
            Tags.Count: 2
        }, "EchoGeneratedAsync");

        var sum = await svc.UploadAsync(ToAsyncEnumerable([1, 2, 3, 4], CancellationToken.None));
        Ensure(sum == 10, "UploadAsync");

        var values = await CollectAsync(svc.DownloadAsync(3), CancellationToken.None);
        Ensure(values.SequenceEqual(["v-0", "v-1", "v-2"]), "DownloadAsync");

        await svc.NotifyAsync("ok");
    }

    [Test]
    public async Task NegotiatedCustomProviderShouldCompressUnaryRequestAndResponse()
    {
        var clientProvider = new CountingCompressionProvider(new TestCompressionProvider());
        var serverProvider = new CountingCompressionProvider(new TestCompressionProvider());
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(clientProvider),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(serverProvider));

        var source = new Person
        {
            Name = new string('a', 16 * 1024),
            Age = 7,
            Tags = [new string('b', 4096)]
        };
        var response = await harness.Client.Get<ITestService>().EchoAsync(source);

        Ensure(response.Name == source.Name + "-r", "compressed unary response");
        Ensure(clientProvider.CompressCount > 0 && clientProvider.DecompressCount > 0,
            "client compression provider should handle both directions");
        Ensure(serverProvider.CompressCount > 0 && serverProvider.DecompressCount > 0,
            "server compression provider should handle both directions");
    }

    [Test]
    public async Task EncodeOnlyTuningMayDifferAcrossOneNegotiatedWireProfile()
    {
        var clientProvider = new CountingCompressionProvider(new TestCompressionProvider(maxRunLength: 64));
        var serverProvider = new CountingCompressionProvider(new TestCompressionProvider(maxRunLength: 128));
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(clientProvider),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(serverProvider));

        var payload = Enumerable.Repeat((byte)0x2a, 16 * 1024).ToArray();
        var response = await harness.Client.Get<ICompressionService>().EchoBytesAsync(payload);

        Ensure(response.SequenceEqual(payload), "different local encode-only tuning");
        Ensure(clientProvider.CompressCount > 0 && clientProvider.DecompressCount > 0,
            "client should encode and decode with its local provider configuration");
        Ensure(serverProvider.CompressCount > 0 && serverProvider.DecompressCount > 0,
            "server should encode and decode with its local provider configuration");
    }

    [Test]
    public async Task ServerProviderOrderShouldSelectFirstMutualWireProfile()
    {
        var clientAlternate = new CountingCompressionProvider(
            new TestCompressionProvider(), "test.rle/alternate");
        var clientPreferred = new CountingCompressionProvider(new TestCompressionProvider());
        var serverPreferred = new CountingCompressionProvider(new TestCompressionProvider());
        var serverAlternate = new CountingCompressionProvider(
            new TestCompressionProvider(), "test.rle/alternate");
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options =>
            {
                options.Compression.Providers.Add(clientAlternate);
                options.Compression.Providers.Add(clientPreferred);
            },
            serverRuntimeConfigure: options =>
            {
                options.Compression.Providers.Add(serverPreferred);
                options.Compression.Providers.Add(serverAlternate);
            });

        var result = await harness.Client.Get<ICompressionService>()
            .EchoBytesAsync(Enumerable.Repeat((byte)3, 4096).ToArray());
        Ensure(result.Length == 4096, "provider preference call");
        Ensure(clientPreferred.CompressCount > 0 && serverPreferred.DecompressCount > 0,
            "server-first mutual provider should be selected");
        Ensure(clientAlternate.CompressCount == 0 && serverAlternate.DecompressCount == 0,
            "lower-priority provider should remain idle");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task OneSidedOrDisjointCompressionShouldFallBackToRawFrames(bool oneSided)
    {
        var clientProvider = new CountingCompressionProvider(
            new TestCompressionProvider(), "test.rle/client-only");
        var serverProvider = new CountingCompressionProvider(new TestCompressionProvider());
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(clientProvider),
            serverRuntimeConfigure: oneSided
                ? null
                : options => options.Compression.Providers.Add(serverProvider));

        var result = await harness.Client.Get<ITestService>().EchoAsync(new Person
        {
            Name = new string('x', 4096),
            Age = 1,
            Tags = ["fallback"]
        });

        Ensure(result.Age == 2, "fallback unary result");
        Ensure(clientProvider.CompressCount == 0 && clientProvider.DecompressCount == 0,
            "unselected client provider must remain idle");
        Ensure(serverProvider.CompressCount == 0 && serverProvider.DecompressCount == 0,
            "unselected server provider must remain idle");
    }

    [Test]
    public async Task NegotiatedCompressionShouldCoverOneWayAndEveryStreamingShape()
    {
        CompressionService.ResetOneWay();
        var clientProvider = new CountingCompressionProvider(new TestCompressionProvider());
        var serverProvider = new CountingCompressionProvider(new TestCompressionProvider());
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(clientProvider),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(serverProvider));
        var service = harness.Client.Get<ICompressionService>();
        var payload = Enumerable.Repeat((byte)0x2a, 8192).ToArray();

        var unary = await service.EchoBytesAsync(payload);
        Ensure(unary.SequenceEqual(payload), "compressed unary bytes");

        await service.NotifyBytesAsync(payload);
        Ensure(await CompressionService.WaitForOneWayAsync().WaitAsync(TimeSpan.FromSeconds(2)) == payload.Length,
            "compressed one-way execution");

        var upload = await service.UploadBytesAsync(
            ToAsyncEnumerable([payload, payload, payload], CancellationToken.None));
        Ensure(upload == payload.Length * 3, "compressed client stream");

        var download = await CollectAsync(service.DownloadBytesAsync(3, payload.Length), CancellationToken.None);
        Ensure(download.Count == 3 && download.All(item => item.SequenceEqual(payload)),
            "compressed server stream");

        var duplex = await CollectAsync(
            service.DuplexBytesAsync(ToAsyncEnumerable([payload, payload], CancellationToken.None)),
            CancellationToken.None);
        Ensure(duplex.Count == 2 && duplex.All(item => item.SequenceEqual(payload)),
            "compressed duplex stream");

        Ensure(clientProvider.CompressCount >= 5 && clientProvider.DecompressCount >= 5,
            "client provider should cover streaming frames");
        Ensure(serverProvider.CompressCount >= 5 && serverProvider.DecompressCount >= 5,
            "server provider should cover streaming frames");
    }

    [Test]
    public async Task ProviderCanRejectBoundedCandidateThroughPublicTryContract()
    {
        var clientProvider = new CountingCompressionProvider(new RejectingCompressionProvider());
        var serverProvider = new CountingCompressionProvider(new RejectingCompressionProvider());
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(clientProvider),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(serverProvider));
        var payload = Enumerable.Repeat((byte)0x4a, 4096).ToArray();

        var response = await harness.Client.Get<ICompressionService>().EchoBytesAsync(payload);

        Ensure(response.SequenceEqual(payload), "public TryCompress=false raw fallback");
        Ensure(clientProvider.CompressCount > 0 && serverProvider.CompressCount > 0,
            "both peers should evaluate the bounded candidate");
        Ensure(clientProvider.DecompressCount == 0 && serverProvider.DecompressCount == 0,
            "a rejected candidate must never be sent as compressed data");
    }

    [Test]
    public async Task SmallOrUnprofitablePayloadShouldRemainUncompressed()
    {
        var clientProvider = new CountingCompressionProvider(new NoBenefitCompressionProvider());
        var serverProvider = new CountingCompressionProvider(new NoBenefitCompressionProvider());
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(clientProvider),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(serverProvider));
        var service = harness.Client.Get<ICompressionService>();

        var small = Enumerable.Repeat((byte)1, 128).ToArray();
        Ensure((await service.EchoBytesAsync(small)).SequenceEqual(small), "small raw fallback");
        Ensure(clientProvider.CompressCount == 0 && serverProvider.CompressCount == 0,
            "small payload should bypass providers");

        var large = Enumerable.Range(0, 4096).Select(static value => (byte)value).ToArray();
        Ensure((await service.EchoBytesAsync(large)).SequenceEqual(large), "unprofitable raw fallback");
        Ensure(clientProvider.CompressCount > 0 && serverProvider.CompressCount > 0,
            "large payload should evaluate provider benefit");
        Ensure(clientProvider.DecompressCount == 0 && serverProvider.DecompressCount == 0,
            "unprofitable candidates must not reach the peer decoder");
    }

    [Test]
    public async Task CompressionProviderFailureShouldFailOneCallAndKeepConnectionHealthy()
    {
        var clientProvider = new ThrowingCompressionProvider(
            new TestCompressionProvider(), throwOnCompress: true, throwOnDecompress: false);
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(clientProvider),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(
                new TestCompressionProvider()));
        var service = harness.Client.Get<ICompressionService>();

        await EnsureThrowsSharpLinkFast(
            service.EchoBytesAsync(Enumerable.Repeat((byte)7, 4096).ToArray()).AsTask(),
            "custom compression failure",
            SharpLinkErrorCode.Internal);
        Ensure((await service.EchoBytesAsync(new byte[] { 1, 2, 3 })).SequenceEqual(new byte[] { 1, 2, 3 }),
            "connection should remain healthy after local compression failure");
    }

    [Test]
    public async Task DecompressionProviderFailureShouldReturnInternalAndKeepConnectionHealthy()
    {
        var serverProvider = new ThrowingCompressionProvider(
            new TestCompressionProvider(), throwOnCompress: false, throwOnDecompress: true);
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(
                new TestCompressionProvider()),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(serverProvider));
        var service = harness.Client.Get<ICompressionService>();

        await EnsureThrowsSharpLinkFast(
            service.EchoBytesAsync(Enumerable.Repeat((byte)7, 4096).ToArray()).AsTask(),
            "custom decompression failure",
            SharpLinkErrorCode.Internal);
        Ensure((await service.EchoBytesAsync(new byte[] { 4, 5, 6 })).SequenceEqual(new byte[] { 4, 5, 6 }),
            "connection should remain healthy after remote decompression failure");
    }

    [Test]
    public async Task ServerCompressionProviderFailureShouldFailUnaryAndKeepConnectionHealthy()
    {
        var serverProvider = new ThrowingCompressionProvider(
            new TestCompressionProvider(), throwOnCompress: true, throwOnDecompress: false);
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(
                new TestCompressionProvider()),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(serverProvider));
        var service = harness.Client.Get<ICompressionService>();

        await EnsureThrowsSharpLinkFast(
            service.EchoBytesAsync(Enumerable.Repeat((byte)7, 4096).ToArray()).AsTask(),
            "server compression failure",
            SharpLinkErrorCode.Internal);
        Ensure((await service.EchoBytesAsync(new byte[] { 7, 8, 9 })).SequenceEqual(new byte[] { 7, 8, 9 }),
            "connection should remain healthy after response compression failure");
    }

    [Test]
    public async Task CompressedServerStreamDecodeFailureShouldReleasePendingCall()
    {
        var clientProvider = new ThrowingCompressionProvider(
            new TestCompressionProvider(), throwOnCompress: false, throwOnDecompress: true);
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(clientProvider),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(
                new TestCompressionProvider()));
        var service = harness.Client.Get<ICompressionService>();

        await EnsureThrowsSharpLinkFast(
            CollectAsync(service.DownloadBytesAsync(100, 4096), CancellationToken.None),
            "compressed server stream decode failure",
            SharpLinkErrorCode.Internal);
        Ensure((await service.EchoBytesAsync(new byte[] { 3, 2, 1 })).SequenceEqual(new byte[] { 3, 2, 1 }),
            "connection should remain healthy after stream decompression failure");
    }
}
