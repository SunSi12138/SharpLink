namespace SharpLink.IntegrationTests;

public class IntegrationBehaviorTests
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

        var paddingIsCanonical = field.WireType == RpcGeneratedWireType.Fixed16 && field.Length == 16 &&
                                 field.Offset + field.Length <= serialized.Length &&
                                 serialized.AsSpan(field.Offset + sizeof(short), 6).IndexOfAnyExcept((byte)0) < 0;
        Ensure(paddingIsCanonical &&
               malformedFailure is SharpLinkException { Code: SharpLinkErrorCode.DataLoss },
            "generated DateTimeOffset must clear native padding and reject invalid ticks");
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

    private static Exception? DeserializeMutatedGeneratedSemantic(
        uint fieldId,
        Action<byte[], int, int> mutate)
    {
        var payload = SerializeGeneratedSemantic();
        var field = FindGeneratedSemanticField(payload, fieldId);
        mutate(payload, field.Offset, field.Length);
        using var context = new SharpLinkRuntimeContextBuilder().Build();
        var codec = context.Codecs.GetCodec<GeneratedSemanticEnvelope>();
        return CaptureException(() => codec.Deserialize(new ReadOnlySequence<byte>(payload)));
    }

    private static byte[] SerializeGeneratedSemantic()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build();
        var codec = context.Codecs.GetCodec<GeneratedSemanticEnvelope>();
        using var writer = new PooledByteBufferWriter();
        codec.Serialize(new GeneratedSemanticEnvelope(
            true,
            new System.Text.Rune('A'),
            123.45m,
            new DateOnly(2026, 7, 27),
            new DateTime(2026, 7, 27, 12, 34, 56, DateTimeKind.Utc),
            new TimeOnly(12, 34, 56),
            CreateDateTimeOffsetWithPoisonedPadding()), writer);
        return writer.WrittenMemory.ToArray();
    }

    private static DateTimeOffset CreateDateTimeOffsetWithPoisonedPadding()
    {
        var value = new DateTimeOffset(2026, 7, 27, 12, 34, 56, TimeSpan.FromHours(8));
        Span<byte> bytes = stackalloc byte[16];
        bytes.Fill(0xA5);
        BinaryPrimitives.WriteInt16LittleEndian(bytes, checked((short)value.Offset.TotalMinutes));
        BinaryPrimitives.WriteInt64LittleEndian(bytes[sizeof(long)..], value.UtcTicks);
        return System.Runtime.InteropServices.MemoryMarshal.Read<DateTimeOffset>(bytes);
    }

    private static (int Offset, int Length, RpcGeneratedWireType WireType) FindGeneratedSemanticField(
        byte[] payload,
        uint targetFieldId)
    {
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(payload));
        Ensure(RpcGeneratedCodecWire.ReadPresence(ref reader), "generated semantic envelope presence");
        while (RpcGeneratedCodecWire.TryReadField(ref reader, out var fieldId, out var wireType))
        {
            var fixedLength = wireType switch
            {
                RpcGeneratedWireType.Fixed1 => 1,
                RpcGeneratedWireType.Fixed2 => 2,
                RpcGeneratedWireType.Fixed4 => 4,
                RpcGeneratedWireType.Fixed8 => 8,
                RpcGeneratedWireType.Fixed16 => 16,
                _ => 0
            };
            if (wireType == RpcGeneratedWireType.LengthDelimited)
            {
                var before = checked((int)reader.Consumed);
                var value = RpcGeneratedCodecWire.ReadLengthDelimited(ref reader);
                if (fieldId == targetFieldId)
                    return (before + sizeof(uint), checked((int)value.Length), wireType);
                continue;
            }
            if (fieldId == targetFieldId)
                return (checked((int)reader.Consumed), fixedLength, wireType);
            RpcGeneratedCodecWire.SkipField(ref reader, wireType);
        }
        throw new Exception($"generated semantic field {targetFieldId} was not found");
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
    public async Task NegotiatedBrotliShouldCompressUnaryRequestAndResponse()
    {
        var clientProvider = new CountingCompressionProvider(SharpLinkCompressionProviders.CreateBrotli());
        var serverProvider = new CountingCompressionProvider(SharpLinkCompressionProviders.CreateBrotli());
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
    public async Task EncodingLevelsMayDifferAcrossOneNegotiatedWireProfile()
    {
        var clientProvider = new CountingCompressionProvider(SharpLinkCompressionProviders.CreateBrotli(
            System.IO.Compression.CompressionLevel.Optimal));
        var serverProvider = new CountingCompressionProvider(SharpLinkCompressionProviders.CreateBrotli(
            System.IO.Compression.CompressionLevel.SmallestSize));
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(clientProvider),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(serverProvider));

        var payload = Enumerable.Repeat((byte)0x2a, 16 * 1024).ToArray();
        var response = await harness.Client.Get<ICompressionService>().EchoBytesAsync(payload);

        Ensure(response.SequenceEqual(payload), "different local encoding levels");
        Ensure(clientProvider.CompressCount > 0 && clientProvider.DecompressCount > 0,
            "client should encode and decode with its local provider configuration");
        Ensure(serverProvider.CompressCount > 0 && serverProvider.DecompressCount > 0,
            "server should encode and decode with its local provider configuration");
    }

    [Test]
    public async Task ServerProviderOrderShouldSelectFirstMutualWireProfile()
    {
        var clientAlternate = new CountingCompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli(), "test.brotli/alternate");
        var clientBrotli = new CountingCompressionProvider(SharpLinkCompressionProviders.CreateBrotli());
        var serverBrotli = new CountingCompressionProvider(SharpLinkCompressionProviders.CreateBrotli());
        var serverAlternate = new CountingCompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli(), "test.brotli/alternate");
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options =>
            {
                options.Compression.Providers.Add(clientAlternate);
                options.Compression.Providers.Add(clientBrotli);
            },
            serverRuntimeConfigure: options =>
            {
                options.Compression.Providers.Add(serverBrotli);
                options.Compression.Providers.Add(serverAlternate);
            });

        var result = await harness.Client.Get<ICompressionService>()
            .EchoBytesAsync(Enumerable.Repeat((byte)3, 4096).ToArray());
        Ensure(result.Length == 4096, "provider preference call");
        Ensure(clientBrotli.CompressCount > 0 && serverBrotli.DecompressCount > 0,
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
            SharpLinkCompressionProviders.CreateBrotli(), "test.brotli/client-only");
        var serverProvider = new CountingCompressionProvider(SharpLinkCompressionProviders.CreateBrotli());
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
        var clientProvider = new CountingCompressionProvider(SharpLinkCompressionProviders.CreateBrotli());
        var serverProvider = new CountingCompressionProvider(SharpLinkCompressionProviders.CreateBrotli());
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
            SharpLinkCompressionProviders.CreateBrotli(), throwOnCompress: true, throwOnDecompress: false);
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(clientProvider),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(
                SharpLinkCompressionProviders.CreateBrotli()));
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
            SharpLinkCompressionProviders.CreateBrotli(), throwOnCompress: false, throwOnDecompress: true);
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(
                SharpLinkCompressionProviders.CreateBrotli()),
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
            SharpLinkCompressionProviders.CreateBrotli(), throwOnCompress: true, throwOnDecompress: false);
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(
                SharpLinkCompressionProviders.CreateBrotli()),
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
            SharpLinkCompressionProviders.CreateBrotli(), throwOnCompress: false, throwOnDecompress: true);
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(clientProvider),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(
                SharpLinkCompressionProviders.CreateBrotli()));
        var service = harness.Client.Get<ICompressionService>();

        await EnsureThrowsSharpLinkFast(
            CollectAsync(service.DownloadBytesAsync(100, 4096), CancellationToken.None),
            "compressed server stream decode failure",
            SharpLinkErrorCode.Internal);
        Ensure((await service.EchoBytesAsync(new byte[] { 3, 2, 1 })).SequenceEqual(new byte[] { 3, 2, 1 }),
            "connection should remain healthy after stream decompression failure");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OneByteFlowWindowsShouldResumeBothStreamDirections(bool useSharedMemory)
    {
        await using var harness = await TestHarness.CreateAsync(runtimeConfigure: options =>
        {
            options.FlowControl.StreamReceiveWindowBytes = 1;
            options.FlowControl.ConnectionReceiveWindowBytes = 1;
        }, useSharedMemory: useSharedMemory);
        var svc = harness.Client.Get<ITestService>();

        var upload = await svc.UploadAsync(
            ToAsyncEnumerable(Enumerable.Range(1, 64), CancellationToken.None));
        Ensure(upload == 2080, "one-byte client stream flow control");

        var download = await CollectAsync(svc.DownloadAsync(64), CancellationToken.None);
        Ensure(download.Count == 64 && download[63] == "v-63", "one-byte server stream flow control");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConnectionPoolShouldExpandOnceUnderConcurrentPressure(bool useSharedMemory)
    {
        await using var harness = await TestHarness.CreateAsync(poolConfigure: options =>
        {
            options.MinConnections = 1;
            options.MaxConnections = 2;
        }, useSharedMemory: useSharedMemory);
        var client = (SharpLinkClient)harness.Client;
        var svc = harness.Client.Get<ITestService>();

        var first = svc.SlowAddWithoutTimeoutAsync(20, 1).AsTask();
        await Task.Delay(20);
        var second = svc.SlowAddWithoutTimeoutAsync(20, 1).AsTask();
        await WaitUntilAsync(() => client.ReadyConnectionCount == 2);

        Ensure(await first == 21 && await second == 21, "concurrent calls should complete across the pool");
        Ensure(client.ReadyConnectionCount == 2, "pressure should create one bounded expansion connection");
    }

    [Test]
    public async Task TwoClientServerPairsShouldUseIndependentDtoCodecs()
    {
        var firstCodec = new MarkerPersonCodec(0xA1);
        var secondCodec = new MarkerPersonCodec(0xB2);
        await using var first = await TestHarness.CreateAsync(personCodec: firstCodec);
        await using var second = await TestHarness.CreateAsync(personCodec: secondCodec);

        var firstResult = await first.Client.Get<ITestService>()
            .EchoAsync(new Person { Name = "first", Age = 1, Tags = ["a"] });
        var secondResult = await second.Client.Get<ITestService>()
            .EchoAsync(new Person { Name = "second", Age = 2, Tags = ["b"] });

        Ensure(firstResult is { Name: "first-r", Age: 2 }, "first context codec");
        Ensure(secondResult is { Name: "second-r", Age: 3 }, "second context codec");
        Ensure(firstCodec.SerializeCount > 0 && firstCodec.DeserializeCount > 0, "first codec should be used");
        Ensure(secondCodec.SerializeCount > 0 && secondCodec.DeserializeCount > 0, "second codec should be used");
    }

    [Test]
    public async Task OneWayMethodTimeoutShouldCancelServerInvocationCooperatively()
    {
        TestService.ResetOneWayDeadlineCancellation();
        await using var harness = await TestHarness.CreateAsync();
        var service = harness.Client.Get<ITestService>();

        await service.WaitForOneWayDeadlineAsync(CancellationToken.None);
        await TestService.WaitForOneWayDeadlineCancellationAsync()
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task UserCancellationShouldPropagateOperationCanceledException()
    {
        await using var harness = await TestHarness.CreateAsync();
        var svc = harness.Client.Get<ITestService>();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await EnsureThrows<OperationCanceledException>(
            svc.SlowAddAsync(1, 2, cts.Token).AsTask(),
            "SlowAddAsync user cancellation");
    }

    [Test]
    [NotInParallel]
    public async Task UnaryResponseAndCallerCancellationRaceShouldHaveOneTerminalOutcomeAndNoPendingLeaks()
    {
        const int callCount = 100;
        TestService.ResetBlockingAdd(callCount);
        await using var harness = await TestHarness.CreateAsync(disableRequestTimeout: true);
        var client = (SharpLinkClient)harness.Client;
        var service = harness.Client.Get<ITestService>();
        var cancellations = Enumerable.Range(0, callCount)
            .Select(static _ => new CancellationTokenSource())
            .ToArray();
        try
        {
            var calls = cancellations.Select((cancellation, iteration) =>
                    service.BlockingAddAsync(iteration, 1, cancellation.Token).AsTask())
                .ToArray();
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(10));

            using var ready = new CountdownEvent(2);
            using var start = new ManualResetEventSlim(initialState: false);
            var response = Task.Run(() =>
            {
                ready.Signal();
                start.Wait();
                TestService.ReleaseBlockingAdd();
            });
            var callerCancel = Task.Run(() =>
            {
                ready.Signal();
                start.Wait();
                foreach (var cancellation in cancellations)
                    cancellation.Cancel();
            });
            Ensure(ready.Wait(TimeSpan.FromSeconds(10)), "P2-T01 workers reached the response/cancel gate");
            start.Set();
            await Task.WhenAll(response, callerCancel).WaitAsync(TimeSpan.FromSeconds(10));

            for (var iteration = 0; iteration < calls.Length; iteration++)
            {
                var exception = await CaptureExceptionAsync(calls[iteration]);
                Ensure(exception is null or OperationCanceledException,
                    $"P2-T01 iteration {iteration}: terminal is success or caller cancellation");
                if (exception is null)
                {
                    Ensure(calls[iteration].Result == iteration + 1,
                        $"P2-T01 iteration {iteration}: successful response value");
                }
            }

            Ensure(client.PendingCallCount == 0 && client.ActiveClientCallCount == 0 &&
                   client.ActiveClientStreamCount == 0,
                "P2-T01: every racing invocation releases pending/call/stream state");
            Ensure(await service.AddAsync(20, 22) == 42,
                "P2-T01: the connection remains reusable after all terminal races");
            await StopHarnessAndAssertResourcesAsync(harness, "P2-T01");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
            foreach (var cancellation in cancellations)
                cancellation.Dispose();
        }
    }

    [Test]
    public async Task DefaultRequestTimeoutShouldThrowDeadlineExceeded()
    {
        await using var harness = await TestHarness.CreateAsync(requestTimeout: TimeSpan.FromMilliseconds(120));
        var svc = harness.Client.Get<ITestService>();

        await EnsureThrowsSharpLinkFast(
            svc.SlowAddAsync(1, 2, CancellationToken.None).AsTask(),
            "SlowAddAsync request timeout",
            SharpLinkErrorCode.DeadlineExceeded);
    }

    [Test]
    [NotInParallel]
    public async Task UnaryWithoutTimeoutAttributeShouldUseClientDefaultTimeout()
    {
        TestService.ResetNonCancellableCompletion();
        await using var harness = await TestHarness.CreateAsync(requestTimeout: TimeSpan.FromMilliseconds(120));
        var svc = harness.Client.Get<ITestService>();

        await EnsureThrowsSharpLinkFast(
            svc.SlowAddWithoutTimeoutAsync(1, 2).AsTask(),
            "SlowAddWithoutTimeoutAsync default timeout",
            SharpLinkErrorCode.DeadlineExceeded);

        await TestService.WaitForNonCancellableCompletionAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(await svc.AddAsync(20, 22) == 42,
            "a late non-cancellable result must be suppressed without damaging the connection");
    }

    [Test]
    [NotInParallel]
    public async Task NonCancellableFailureAfterTimeoutShouldBeObservedAndSuppressed()
    {
        TestService.ResetNonCancellableFailure();
        await using var harness = await TestHarness.CreateAsync(requestTimeout: TimeSpan.FromMilliseconds(120));
        var svc = harness.Client.Get<ITestService>();

        await EnsureThrowsSharpLinkFast(
            svc.SlowThrowWithoutTimeoutAsync().AsTask(),
            "SlowThrowWithoutTimeoutAsync default timeout",
            SharpLinkErrorCode.DeadlineExceeded);

        await TestService.WaitForNonCancellableFailureAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(await svc.AddAsync(20, 22) == 42,
            "a late non-cancellable exception must be observed without damaging the connection");
    }

    [Test]
    [NotInParallel]
    public async Task DisableRequestTimeoutShouldAllowNonCancellableUnaryToFinish()
    {
        TestService.ResetNonCancellableCompletion();
        await using var harness = await TestHarness.CreateAsync(disableRequestTimeout: true);
        var svc = harness.Client.Get<ITestService>();

        Ensure(await svc.SlowAddWithoutTimeoutAsync(20, 22) == 42,
            "disabled default timeout should wait for the service result");
        await TestService.WaitForNonCancellableCompletionAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task NonCancellableOperationCanceledExceptionShouldNotBeMisreportedAsDeadline()
    {
        await using var harness = await TestHarness.CreateAsync();
        await EnsureThrowsSharpLinkFast(
            harness.Client.Get<ITestService>().ThrowCancellationAsync().AsTask(),
            "non-cancellable service cancellation classification",
            SharpLinkErrorCode.Cancelled);
    }

    [Test]
    public async Task EarlyServerStreamDisposalShouldCancelAndReleaseConnectionState()
    {
        await using var harness = await TestHarness.CreateAsync();
        var client = (SharpLinkClient)harness.Client;
        var service = harness.Client.Get<ITestService>();

        for (var iteration = 0; iteration < 100; iteration++)
        {
            await using var enumerator = service
                .SlowDownloadAsync(1_000, 10, CancellationToken.None)
                .GetAsyncEnumerator();
            Ensure(await enumerator.MoveNextAsync(), "stream should produce its first item");
        }

        await WaitUntilAsync(() =>
            client.PendingCallCount == 0 &&
            client.ActiveClientCallCount == 0 &&
            client.ActiveClientStreamCount == 0);
        Ensure(await service.AddAsync(20, 22) == 42, "connection should remain healthy after early disposal");
    }

    [Test]
    [NotInParallel]
    public async Task NonCancellableServerStreamEarlyBreakShouldStopFrameworkPump()
    {
        TestService.ResetDownloadDisposed();
        await using var harness = await TestHarness.CreateAsync();
        var service = harness.Client.Get<ITestService>();

        await using (var enumerator = service.DownloadAsync(int.MaxValue).GetAsyncEnumerator())
            Ensure(await enumerator.MoveNextAsync(), "stream should produce its first item");

        await TestService.WaitForDownloadDisposedAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(await service.AddAsync(20, 22) == 42,
            "framework stream cancellation must leave the connection healthy");
    }

    [Test]
    [NotInParallel]
    public async Task FastEarlyBreakShouldReturnFlowCreditAndNotLeakCompletedSendStates()
    {
        await using var harness = await TestHarness.CreateAsync(
            runtimeConfigure: static options =>
                // Amplify completed-state pressure while leaving enough room for a canceled
                // producer to observe its terminal token and retire its final in-flight frame.
                options.Protocol.MaxConcurrentStreamsPerConnection = 8);
        var service = harness.Client.Get<ITestService>();

        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            var enumerator = service.DownloadAsync(32).GetAsyncEnumerator();
            try
            {
                bool hasItem;
                try
                {
                    hasItem = await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException exception)
                {
                    throw new TimeoutException(
                        $"Fast stream {iteration}/10,000 did not produce its first item within 5 seconds.",
                        exception);
                }
                Ensure(hasItem, "fast stream should produce its first item");
            }
            finally
            {
                try
                {
                    await enumerator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException exception)
                {
                    throw new TimeoutException(
                        $"Fast stream {iteration}/10,000 did not dispose within 5 seconds.",
                        exception);
                }
            }
        }

        Ensure(await service.AddAsync(20, 22) == 42,
            "connection should remain healthy after 10,000 fast early-break streams");
    }

    [Test]
    public async Task MethodTimeoutShouldExpireWithoutPublicCallContextDeadline()
    {
        await using var harness = await TestHarness.CreateAsync();
        var svc = harness.Client.Get<ITestService>();

        var summary = await svc.DescribeCallAsync(42, CancellationToken.None);
        Ensure(summary.StartsWith("42:missing:no-deadline", StringComparison.Ordinal),
            "method timeout should not recreate a public absolute call-context deadline");

        await EnsureThrowsSharpLinkFast(
            svc.SlowAddWithMethodTimeoutAsync(1, 2, CancellationToken.None).AsTask(),
            "method timeout",
            SharpLinkErrorCode.DeadlineExceeded);
    }

    [Test]
    public async Task CallerSelectedMetadataShouldVaryPerInvocation()
    {
        await using var harness = await TestHarness.CreateAsync();
        var tenantA = harness.Client.GetWithMetadata<ITestService>(new SharpLinkMetadata(
            new KeyValuePair<string, string>("tenant", "a")));
        var tenantB = harness.Client.GetWithMetadata<ITestService>(new SharpLinkMetadata(
            new KeyValuePair<string, string>("tenant", "b")));

        var results = await Task.WhenAll(
            tenantA.DescribeCallAsync(1, CancellationToken.None).AsTask(),
            tenantB.DescribeCallAsync(2, CancellationToken.None).AsTask());

        Ensure(results[0].StartsWith("1:a:", StringComparison.Ordinal),
            "caller-selected metadata A should stay bound to its invocation");
        Ensure(results[1].StartsWith("2:b:", StringComparison.Ordinal),
            "caller-selected metadata B should stay bound to its invocation");
    }

    [Test]
    [NotInParallel]
    public async Task ServerStopShouldPreservePendingCallCancellationReasonsWithoutReenteringMapper()
    {
        var exceptionMapper = new RecordingServerStreamExceptionMapper();
        for (var iteration = 0; iteration < 10; iteration++)
        {
            await using var harness = await TestHarness.CreateAsync(
                serverConfigure: builder => builder.UseExceptionMapper(exceptionMapper));
            var svc = harness.Client.Get<ITestService>();

            var unaryTask = svc.SlowAddAsync(1, 2, CancellationToken.None).AsTask();
            var streamTask = CollectAsync(
                svc.SlowDownloadAsync(100, 200, CancellationToken.None),
                CancellationToken.None);

            await Task.Delay(100);
            await harness.DisposeServerOnlyAsync();

            await EnsureThrowsSharpLinkFast(
                unaryTask,
                $"unary fail-fast iteration {iteration}",
                SharpLinkErrorCode.Unavailable,
                SharpLinkErrorCode.ConnectionClosed);
            await EnsureThrowsSharpLinkFast(
                streamTask,
                $"stream fail-fast iteration {iteration}",
                SharpLinkErrorCode.Unavailable,
                SharpLinkErrorCode.ConnectionClosed);
        }

        var mappedStreamErrors = exceptionMapper.GetMappedCodes();
        Ensure(mappedStreamErrors.Length == 0,
            "a framework-selected server-stop terminal must not re-enter the application stream exception mapper");
    }

    [Test]
    public async Task ClientDisposeShouldFailFastPendingUnaryAndStream()
    {
        await using var harness = await TestHarness.CreateAsync();
        var svc = harness.Client.Get<ITestService>();

        var unaryTask = svc.SlowAddAsync(1, 2, CancellationToken.None).AsTask();
        var streamTask = CollectAsync(svc.SlowDownloadAsync(100, 200, CancellationToken.None), CancellationToken.None);

        await Task.Delay(100);
        await harness.DisposeClientOnlyAsync();

        await EnsureThrowsSharpLinkFast(unaryTask, "unary fail-fast after client dispose", SharpLinkErrorCode.ConnectionClosed);
        await EnsureThrowsSharpLinkFast(streamTask, "stream fail-fast after client dispose", SharpLinkErrorCode.ConnectionClosed);
    }

    [Test]
    [NotInParallel]
    public async Task ClientStreamingStopRaceShouldReleaseEveryServerInvocation()
    {
        const int callCount = 128;
        TestService.ResetActiveUploads();
        await using var harness = await TestHarness.CreateAsync(poolConfigure: options =>
        {
            options.MinConnections = 1;
            options.MaxConnections = 4;
        });
        var service = harness.Client.Get<ITestService>();
        using var producerCancellation = new CancellationTokenSource();
        var calls = new Task<int>[callCount];
        for (var index = 0; index < calls.Length; index++)
        {
            calls[index] = service.UploadAsync(
                YieldOneThenWaitAsync(index, producerCancellation.Token)).AsTask();
        }

        await WaitUntilAsync(() => TestService.ActiveUploads == callCount);
        var stopServer = harness.DisposeServerOnlyAsync(TimeSpan.Zero).AsTask();
        var stopClient = harness.DisposeClientOnlyAsync().AsTask();
        await producerCancellation.CancelAsync();
        await Task.WhenAll(stopServer, stopClient).WaitAsync(TimeSpan.FromSeconds(10));

        foreach (var call in calls)
        {
            try
            {
                _ = await call.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception exception) when (exception is OperationCanceledException or SharpLinkException)
            {
            }
        }
        await WaitUntilAsync(() => TestService.ActiveUploads == 0);
    }

    [Test]
    [NotInParallel]
    public async Task ClientStreamProducerFailureShouldTerminateRemoteInvocationAndKeepConnectionHealthy()
    {
        TestService.ResetActiveUploads();
        await using var harness = await TestHarness.CreateAsync();
        var service = harness.Client.Get<ITestService>();

        for (var iteration = 0; iteration < 1_000; iteration++)
        {
            await EnsureClientStreamProducerFailure(
                service.UploadAsync(YieldThenFailAsync(iteration)).AsTask(),
                "client stream producer failure");
        }

        await WaitUntilAsync(() => TestService.ActiveUploads == 0);
        Ensure(await service.AddAsync(20, 22) == 42,
            "connection should remain healthy after client stream producer failures");
    }

    [Test]
    [NotInParallel]
    public async Task GracefulStopShouldDrainAcceptedCallAndRejectNewCallsAfterGoAway()
    {
        await using var harness = await TestHarness.CreateAsync();
        var svc = harness.Client.Get<ITestService>();

        var acceptedCall = svc.SlowAddWithoutTimeoutAsync(20, 22).AsTask();
        await Task.Delay(50);
        var stopTask = harness.DisposeServerOnlyAsync(TimeSpan.FromSeconds(2)).AsTask();
        await WaitUntilAsync(() => harness.Client.State == SharpLinkConnectionState.Draining);

        await EnsureThrowsSharpLinkFast(
            svc.AddAsync(1, 1).AsTask(),
            "new call after GoAway",
            SharpLinkErrorCode.Unavailable);
        Ensure(await acceptedCall == 42, "accepted call should complete during grace period");
        await stopTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    [NotInParallel]
    public async Task GracefulStopShouldDrainOneHundredAcceptedCallsAndReleaseResources()
    {
        const int callCount = 100;
        TestService.ResetBlockingAdd(callCount);
        await using var harness = await TestHarness.CreateAsync();
        var svc = harness.Client.Get<ITestService>();
        var acceptedCalls = Enumerable.Range(0, callCount)
            .Select(iteration => svc.BlockingAddAsync(iteration, 1, CancellationToken.None).AsTask())
            .ToArray();
        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var stopTask = harness.DisposeServerOnlyAsync(TimeSpan.FromSeconds(2)).AsTask();
            TestService.ReleaseBlockingAdd();

            var results = await Task.WhenAll(acceptedCalls).WaitAsync(TimeSpan.FromSeconds(10));
            Ensure(results.Where((result, iteration) => result != iteration + 1).Any() is false,
                "P2-T03 grace: all 100 accepted calls complete on their original terminal path");
            await stopTask.WaitAsync(TimeSpan.FromSeconds(10));
            await StopHarnessAndAssertResourcesAsync(harness, "P2-T03 grace");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
        }
    }

    [Test]
    [NotInParallel]
    public async Task GraceTimeoutShouldSelectOneForcedTerminalForOneHundredCallsAndReleaseResources()
    {
        const int callCount = 100;
        TestService.ResetBlockingAdd(callCount);
        await using var harness = await TestHarness.CreateAsync();
        var svc = harness.Client.Get<ITestService>();
        var pending = Enumerable.Range(0, callCount)
            .Select(iteration => svc.BlockingAddAsync(iteration, 1, CancellationToken.None).AsTask())
            .ToArray();
        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var started = Stopwatch.GetTimestamp();
            await harness.DisposeServerOnlyAsync(TimeSpan.Zero).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Ensure(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10),
                "P2-T03 force: zero grace stops within the lifecycle bound");

            for (var iteration = 0; iteration < pending.Length; iteration++)
            {
                var exception = await CaptureExceptionAsync(pending[iteration]);
                Ensure(exception is SharpLinkException
                { Code: SharpLinkErrorCode.ConnectionClosed },
                    $"P2-T03 force iteration {iteration}: ConnectionClosed is the unique wire terminal");
            }
            await StopHarnessAndAssertResourcesAsync(harness, "P2-T03 force");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
        }
    }

    [Test]
    [NotInParallel]
    public async Task ServerConcurrencyExhaustionShouldRejectOverflowAndRecoverWithoutClosingConnection()
    {
        await using var harness = await TestHarness.CreateAsync();
        var svc = harness.Client.Get<ITestService>();
        var calls = new Task<int>[1025];

        for (var index = 0; index < calls.Length; index++)
            calls[index] = svc.SlowAddAsync(index, 1, CancellationToken.None).AsTask();

        var completed = 0;
        var exhausted = 0;
        foreach (var call in calls)
        {
            try
            {
                _ = await call.WaitAsync(TimeSpan.FromSeconds(10));
                completed++;
            }
            catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.ResourceExhausted)
            {
                exhausted++;
            }
        }

        Ensure(completed == 1024, "server should admit exactly the per-connection limit");
        Ensure(exhausted == 1, "server should reject the overflow call as ResourceExhausted");
        Ensure(await svc.AddAsync(20, 22) == 42, "connection should recover after call capacity is released");
    }

    [Test]
    [NotInParallel]
    public async Task AdmissionQueueShouldRemainBoundedAndRecoverOnSameConnection()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(10);
            }));
        var service = harness.Client.Get<ITestService>();

        TestService.ResetBlockingAdd();
        var active = service.BlockingAddAsync(20, 1).AsTask();
        const int contenderCount = 8;
        var contenders = new List<Task<int>>(capacity: contenderCount);
        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(2));
            for (var index = 0; index < contenderCount; index++)
                contenders.Add(service.AddAsync(20, index).AsTask());

            await WaitUntilAsync(() => contenders.Count(static task => task.IsCompleted) >= contenders.Count - 1);
            Ensure(contenders.Count(static task => task.IsCompleted) == contenders.Count - 1,
                "admission queue must retain exactly one call before permit release");
            var queuedIndex = -1;
            for (var index = 0; index < contenders.Count; index++)
            {
                if (contenders[index].IsCompleted)
                {
                    await EnsureThrowsSharpLinkFast(
                        contenders[index],
                        "admission queue count",
                        SharpLinkErrorCode.ResourceExhausted);
                }
                else
                {
                    Ensure(queuedIndex < 0, "admission queue must retain exactly one call");
                    queuedIndex = index;
                }
            }

            Ensure(queuedIndex >= 0, "admission queue must retain one call");
            TestService.ReleaseBlockingAdd();
            Ensure(await active.WaitAsync(TimeSpan.FromSeconds(2)) == 21, "active admitted call");
            Ensure(await contenders[queuedIndex].WaitAsync(TimeSpan.FromSeconds(2)) == 20 + queuedIndex,
                "queued admitted call");
            Ensure(await service.AddAsync(20, 4) == 24, "connection recovers after overload");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
            try
            {
                await Task.WhenAll(contenders).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                _ = contenders.Count(static task => task.Exception is not null);
            }
        }
    }

    [Test]
    [NotInParallel]
    public async Task QueuedClientStreamShouldSpoolUntilAdmissionAndPreserveOrder()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
            }));
        var service = harness.Client.Get<ITestService>();

        var active = service.SlowAddWithoutTimeoutAsync(1, 1).AsTask();
        await Task.Delay(75);
        var queuedStream = service.UploadAsync(
            ToAsyncEnumerable([1, 2, 3, 4], CancellationToken.None)).AsTask();

        Ensure(await active.WaitAsync(TimeSpan.FromSeconds(2)) == 2, "active call before stream");
        Ensure(await queuedStream.WaitAsync(TimeSpan.FromSeconds(2)) == 10,
            "pre-admission stream spool order");
        Ensure(TestService.ActiveUploads == 0, "queued stream permit and dispatcher released");
    }

    [Test]
    [NotInParallel]
    public async Task QueuedDuplexStreamShouldSpoolAndHoldPermitForBothDirections()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
            }));
        var active = harness.Client.Get<ITestService>()
            .SlowAddWithoutTimeoutAsync(1, 1).AsTask();
        await Task.Delay(75);
        var payloads = new[]
        {
            Enumerable.Repeat((byte)0x11, 256).ToArray(),
            Enumerable.Repeat((byte)0x22, 512).ToArray()
        };
        var duplex = CollectAsync(
            harness.Client.Get<ICompressionService>().DuplexBytesAsync(
                ToAsyncEnumerable(payloads, CancellationToken.None)),
            CancellationToken.None);

        Ensure(await active.WaitAsync(TimeSpan.FromSeconds(2)) == 2,
            "active call before queued duplex");
        var received = await duplex.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(received.Count == 2 && received[0].SequenceEqual(payloads[0]) &&
            received[1].SequenceEqual(payloads[1]), "queued duplex preserves both directions");
        Ensure(await harness.Client.Get<ITestService>().AddAsync(20, 22) == 42,
            "duplex releases admission permit");
    }

    [Test]
    [NotInParallel]
    public async Task QueuedCompressedRequestAndClientStreamShouldDecodeAfterAdmission()
    {
        await using var harness = await TestHarness.CreateAsync(
            clientRuntimeConfigure: options => options.Compression.Providers.Add(
                SharpLinkCompressionProviders.CreateBrotli()),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(
                SharpLinkCompressionProviders.CreateBrotli()),
            serverConfigure: builder => builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
            }));

        var active = harness.Client.Get<ITestService>()
            .SlowAddWithoutTimeoutAsync(20, 22).AsTask();
        await Task.Delay(75);
        var item = Enumerable.Repeat((byte)0x2a, 8192).ToArray();
        var upload = harness.Client.Get<ICompressionService>().UploadBytesAsync(
            ToAsyncEnumerable([item, item, item], CancellationToken.None)).AsTask();

        Ensure(await active.WaitAsync(TimeSpan.FromSeconds(2)) == 42,
            "active call before compressed stream");
        Ensure(await upload.WaitAsync(TimeSpan.FromSeconds(2)) == item.Length * 3,
            "queued compressed request and stream items");
        Ensure((await harness.Client.Get<ICompressionService>().EchoBytesAsync([1, 2, 3]))
            .SequenceEqual(new byte[] { 1, 2, 3 }), "combined overload connection recovery");
    }

    [Test]
    public async Task MethodRateLimitShouldNotThrottleOtherMethodsAndShouldRecover()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
                options.AddMethod<ITestService>(nameof(ITestService.AddAsync), rule =>
                    rule.UseFixedWindow(rate =>
                    {
                        rate.PermitLimit = 1;
                        rate.Window = TimeSpan.FromMilliseconds(100);
                    }))));
        var service = harness.Client.Get<ITestService>();

        Ensure(await service.AddAsync(1, 1) == 2, "first method-rate permit");
        await EnsureThrowsSharpLinkFast(
            service.AddAsync(2, 2).AsTask(),
            "method rate rejection",
            SharpLinkErrorCode.ResourceExhausted);
        Ensure((await service.EchoAsync(new Person { Name = "other", Age = 1 })).Age == 2,
            "unlimited method remains healthy");
        using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            while (true)
            {
                try
                {
                    var result = await service.AddAsync(3, 4).AsTask()
                        .WaitAsync(recoveryTimeout.Token);
                    Ensure(result == 7, "method rate replenishment");
                    break;
                }
                catch (SharpLinkException exception) when (
                    exception.Code == SharpLinkErrorCode.ResourceExhausted)
                {
                    await Task.Delay(20, recoveryTimeout.Token);
                }
            }
        }
        catch (OperationCanceledException) when (recoveryTimeout.IsCancellationRequested)
        {
            throw new Exception("assert failed: method rate permit did not replenish within 3 seconds");
        }
    }

    [Test]
    public async Task ContractLimitShouldNotThrottleAnotherContract()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
                options.AddContract<ITestService>(rule => rule.UseConcurrency(1))));
        var testService = harness.Client.Get<ITestService>();
        var active = testService.SlowAddWithoutTimeoutAsync(20, 1).AsTask();
        await Task.Delay(75);

        await EnsureThrowsSharpLinkFast(
            testService.AddAsync(1, 1).AsTask(),
            "contract concurrency rejection",
            SharpLinkErrorCode.ResourceExhausted);
        var other = await harness.Client.Get<ICompressionService>().EchoBytesAsync([7, 8, 9]);
        Ensure(other.SequenceEqual(new byte[] { 7, 8, 9 }), "other contract remains admitted");
        Ensure(await active == 21, "contract permit owner completes");
    }

    [Test]
    public async Task PartitionSelectorShouldIsolateMetadataKeys()
    {
        var metadataInterceptor = new SequencedTenantMetadataInterceptor();
        await using var harness = await TestHarness.CreateAsync(
            serverConfigure: builder => builder.UseAdmissionControl(options => options.UsePartition(
                context => context.Metadata is { Count: > 0 } metadata ? metadata[0].Value : null,
                partition =>
                {
                    partition.MaxPartitions = 8;
                    partition.UseConcurrency(1);
                })),
            clientInterceptor: metadataInterceptor);
        var service = harness.Client.Get<ITestService>();
        TestService.ResetBlockingAdd();
        var active = service.BlockingAddAsync(1, 2, CancellationToken.None).AsTask();
        await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(2));

        await EnsureThrowsSharpLinkFast(
            service.AddAsync(1, 1).AsTask(),
            "same partition concurrency",
            SharpLinkErrorCode.ResourceExhausted);
        Ensure(await service.AddAsync(2, 2) == 4, "independent metadata partition permit");

        TestService.ReleaseBlockingAdd();
        Ensure(await active == 3, "partition active call completion");
    }

    [Test]
    public async Task AdmissionQueueByteLimitShouldRejectBeforeServiceCreation()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 2;
                options.MaxQueuedBytes = 64;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
            }));
        var service = harness.Client.Get<ITestService>();
        var active = service.SlowAddWithoutTimeoutAsync(10, 1).AsTask();
        await Task.Delay(75);

        await EnsureThrowsSharpLinkFast(
            service.EchoAsync(new Person
            {
                Name = new string('x', 2048),
                Age = 1,
                Tags = ["queue-bytes"]
            }).AsTask(),
            "admission queue bytes",
            SharpLinkErrorCode.ResourceExhausted);
        Ensure(await active == 11, "queue-byte permit owner");
        Ensure(await service.AddAsync(20, 22) == 42, "queue-byte rejection connection recovery");
    }

    [Test]
    [NotInParallel]
    public async Task PreAdmissionStreamSpoolShouldRejectWhenRetainedBytesOverflow()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 128;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
            }));
        var service = harness.Client.Get<ITestService>();
        var active = service.SlowAddWithoutTimeoutAsync(10, 1).AsTask();
        await Task.Delay(75);
        var oversized = service.UploadAsync(ToAsyncEnumerable(
            Enumerable.Range(1, 100), CancellationToken.None)).AsTask();

        // The initial request fits, then the pre-admission stream frames consume the
        // remaining retained-byte budget and terminate the call without service execution.
        await EnsureThrowsSharpLinkFast(
            oversized,
            "pre-admission stream retained bytes",
            SharpLinkErrorCode.ResourceExhausted);
        Ensure(TestService.ActiveUploads == 0, "overflowed stream service did not execute");
        Ensure(await active == 11, "spool overflow permit owner");
        Ensure(await service.AddAsync(20, 22) == 42, "spool overflow connection recovery");
    }

    [Test]
    [NotInParallel]
    public async Task QueuedClientStreamCallerCancellationShouldReleaseAdmissionAndStreamResources()
    {
        using var metrics = new LifecycleMetricProbe(
            LifecycleMetricProbe.AdmissionPermits,
            LifecycleMetricProbe.AdmissionQueuedCalls,
            LifecycleMetricProbe.ActiveStreams);
        TestService.ResetActiveUploads();
        TestService.ResetBlockingAdd();
        await using var harness = await TestHarness.CreateAsync(
            serverConfigure: builder => builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(10);
            }));
        var client = (SharpLinkClient)harness.Client;
        var service = harness.Client.Get<ITestService>();
        var active = service.BlockingAddAsync(1, 1, CancellationToken.None).AsTask();
        using var cancellation = new CancellationTokenSource();
        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await metrics.WaitForValueAsync(
                LifecycleMetricProbe.AdmissionPermits, 1, "P2-T04 active permit");
            var queued = service.UploadAsync(
                YieldOneThenWaitAsync(2, cancellation.Token),
                cancellation.Token).AsTask();
            await metrics.WaitForValueAsync(
                LifecycleMetricProbe.AdmissionQueuedCalls, 1, "P2-T04 queued waiter");
            await metrics.WaitForAtLeastAsync(
                LifecycleMetricProbe.ActiveStreams, 1, "P2-T04 pre-admission stream reservation");

            await cancellation.CancelAsync();
            Ensure(await CaptureExceptionAsync(queued) is OperationCanceledException,
                "P2-T04 queued client stream observes caller cancellation");
            await metrics.WaitForValueAsync(
                LifecycleMetricProbe.AdmissionQueuedCalls, 0, "P2-T04 waiter release");
            await metrics.WaitForValueAsync(
                LifecycleMetricProbe.ActiveStreams, 0, "P2-T04 stream reservation release");
            var queuedReleased = ServerLifecycleResourceInspector.Capture(harness.Server);
            Ensure(queuedReleased is
            {
                AdmissionQueuedCalls: 0,
                AdmissionQueuedBytes: 0,
                AdmissionPermits: 1
            },
                "P2-T04 waiter/retained payload/stream reservation release while owner retains one permit");
            Ensure(TestService.ActiveUploads == 0,
                "P2-T04 canceled queued stream never reaches the service");

            TestService.ReleaseBlockingAdd();
            Ensure(await active.WaitAsync(TimeSpan.FromSeconds(10)) == 2,
                "P2-T04 active permit owner completes");
            await metrics.WaitForValueAsync(
                LifecycleMetricProbe.AdmissionPermits, 0, "P2-T04 permit release");
            Ensure(client.PendingCallCount == 0 && client.ActiveClientCallCount == 0 &&
                   client.ActiveClientStreamCount == 0,
                "P2-T04 client pending/call/stream resources return to zero");
            Ensure(await service.AddAsync(3, 4) == 7,
                "P2-T04 released admission capacity is reusable");
            await StopHarnessAndAssertResourcesAsync(harness, "P2-T04");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
            await cancellation.CancelAsync();
        }
    }

    [Test]
    [NotInParallel]
    public async Task QueuedDeadlineShouldNotLeakPermits()
    {
        TestService.ResetNonCancellableCompletion();
        await using var deadlineHarness = await TestHarness.CreateAsync(
            requestTimeout: TimeSpan.FromMilliseconds(100),
            serverConfigure: builder => builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 4096;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
            }));
        var deadlineService = deadlineHarness.Client.Get<ITestService>();
        var deadlineActive = deadlineService.SlowAddWithoutTimeoutAsync(1, 1).AsTask();
        await Task.Delay(25);
        await EnsureThrowsSharpLinkFast(
            deadlineService.AddAsync(2, 2).AsTask(),
            "queued deadline",
            SharpLinkErrorCode.DeadlineExceeded);
        await EnsureThrowsSharpLinkFast(
            deadlineActive,
            "active default deadline",
            SharpLinkErrorCode.DeadlineExceeded);
        await TestService.WaitForNonCancellableCompletionAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    [NotInParallel]
    public async Task RejectedAndQueuedOneWayCallsShouldFollowConfiguredPolicy()
    {
        TestService.ResetNotify();
        await using (var rejectHarness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options => options.Global.UseConcurrency(1))))
        {
            var service = rejectHarness.Client.Get<ITestService>();
            var active = service.SlowAddWithoutTimeoutAsync(1, 1).AsTask();
            await Task.Delay(75);
            await service.NotifyAsync("drop");
            await Task.Delay(75);
            Ensure(TestService.NotifyCount == 0, "rejected OneWay service must not execute");
            Ensure(await active == 2, "OneWay rejection permit owner");
        }

        TestService.ResetNotify();
        await using var queueHarness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 4096;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
                options.QueueOneWayCalls = true;
            }));
        var queuedService = queueHarness.Client.Get<ITestService>();
        var queuedActive = queuedService.SlowAddWithoutTimeoutAsync(2, 2).AsTask();
        await Task.Delay(75);
        await queuedService.NotifyAsync("queue");
        Ensure(await queuedActive == 4, "queued OneWay permit owner");
        await TestService.WaitForNotifyAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(TestService.NotifyCount == 1, "explicitly queued OneWay executes once");
    }

    [Test]
    [NotInParallel]
    public async Task QueuedOneWayClientStreamShouldSpoolUntilAdmission()
    {
        CompressionService.ResetOneWay();
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
                options.QueueOneWayCalls = true;
            }));
        var active = harness.Client.Get<ITestService>()
            .SlowAddWithoutTimeoutAsync(4, 5).AsTask();
        await Task.Delay(75);
        var payloads = new[]
        {
            Enumerable.Repeat((byte)0x31, 128).ToArray(),
            Enumerable.Repeat((byte)0x32, 256).ToArray()
        };

        await harness.Client.Get<ICompressionService>().NotifyStreamBytesAsync(
            ToAsyncEnumerable(payloads, CancellationToken.None));

        Ensure(await active.WaitAsync(TimeSpan.FromSeconds(2)) == 9,
            "queued OneWay client-stream permit owner");
        Ensure(await CompressionService.WaitForOneWayAsync().WaitAsync(TimeSpan.FromSeconds(2)) == 384,
            "queued OneWay client-stream items preserved");
        Ensure(await harness.Client.Get<ITestService>().AddAsync(20, 22) == 42,
            "queued OneWay client-stream connection recovery");
    }

    [Test]
    [NotInParallel]
    public async Task RejectedOneWayClientStreamShouldDrainWithoutServiceExecution()
    {
        CompressionService.ResetOneWay();
        await using var harness = await TestHarness.CreateAsync(
            runtimeConfigure: options =>
            {
                options.FlowControl.StreamReceiveWindowBytes = 64;
                options.FlowControl.ConnectionReceiveWindowBytes = 64;
            },
            serverConfigure: builder => builder.UseAdmissionControl(options =>
                options.Global.UseConcurrency(1)));
        var active = harness.Client.Get<ITestService>()
            .SlowAddWithoutTimeoutAsync(6, 7).AsTask();
        await Task.Delay(75);
        var payloads = Enumerable.Range(0, 256)
            .Select(static index => Enumerable.Repeat((byte)index, 128).ToArray());

        await harness.Client.Get<ICompressionService>()
            .NotifyStreamBytesAsync(ToAsyncEnumerable(payloads, CancellationToken.None))
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(await active.WaitAsync(TimeSpan.FromSeconds(2)) == 13,
            "rejected OneWay stream permit owner");
        await Task.Delay(100);
        Ensure(!CompressionService.WaitForOneWayAsync().IsCompleted,
            "rejected OneWay stream service must not execute");
        Ensure(await harness.Client.Get<ITestService>().AddAsync(20, 22) == 42,
            "rejected OneWay stream connection recovery");
    }

    [Test]
    [NotInParallel]
    public async Task PostAdmissionArgumentDecodeFailureShouldReleaseReservedStreams()
    {
        TestService.ResetMalformedUploadInvocations();
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
        {
            builder.UseCodec(new ThrowingPersonCodec());
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
            });
        });
        var service = harness.Client.Get<ITestService>();
        var active = service.SlowAddWithoutTimeoutAsync(8, 9).AsTask();
        await Task.Delay(75);
        var failed = service.UploadWithHeaderAsync(
            new Person { Name = "malformed", Age = 1 },
            ToAsyncEnumerable(Enumerable.Range(1, 256), CancellationToken.None)).AsTask();

        await EnsureThrowsSharpLinkFast(
            failed,
            "post-admission argument decode failure",
            SharpLinkErrorCode.Internal);

        Ensure(await active.WaitAsync(TimeSpan.FromSeconds(2)) == 17,
            "post-admission decode permit owner");
        Ensure(TestService.MalformedUploadInvocations == 0,
            "malformed request service must not execute");
        Ensure(await service.AddAsync(20, 22) == 42,
            "post-admission decode failure connection recovery");
    }

    [Test]
    [NotInParallel]
    public async Task QueuedOneWayRequestDecompressionFailureShouldDrainReservedStreams()
    {
        CompressionService.ResetOneWay();
        var serverProvider = new ThrowingCompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli(), throwOnCompress: false, throwOnDecompress: true);
        await using var harness = await TestHarness.CreateAsync(
            runtimeConfigure: options =>
            {
                options.FlowControl.StreamReceiveWindowBytes = 64;
                options.FlowControl.ConnectionReceiveWindowBytes = 64;
            },
            clientRuntimeConfigure: options => options.Compression.Providers.Add(
                SharpLinkCompressionProviders.CreateBrotli()),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(serverProvider),
            serverConfigure: builder => builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024;
                options.MaxQueueDelay = TimeSpan.FromSeconds(2);
                options.QueueOneWayCalls = true;
            }));
        var permitOwner = harness.Client.Get<ITestService>()
            .SlowAddWithoutTimeoutAsync(9, 10).AsTask();
        await Task.Delay(75);
        var payloads = Enumerable.Range(0, 256)
            .Select(static index => Enumerable.Repeat((byte)index, 128).ToArray());
        var failedOneWay = harness.Client.Get<ICompressionService>()
            .NotifyStreamWithHeaderAsync(
                Enumerable.Repeat((byte)0x41, 4096).ToArray(),
                ToAsyncEnumerable(payloads, CancellationToken.None))
            .AsTask();

        Ensure(await permitOwner.WaitAsync(TimeSpan.FromSeconds(2)) == 19,
            "queued compressed OneWay permit owner");
        await failedOneWay.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        Ensure(!CompressionService.WaitForOneWayAsync().IsCompleted,
            "failed compressed OneWay request must not execute the service");
        Ensure(await harness.Client.Get<ITestService>().AddAsync(20, 22) == 42,
            "compressed OneWay decode failure connection recovery");
    }

    [Test]
    [NotInParallel]
    public async Task OneWayRequestDecompressionFailureWithoutAdmissionShouldDrainClientStreams()
    {
        CompressionService.ResetOneWay();
        var serverProvider = new ThrowingCompressionProvider(
            SharpLinkCompressionProviders.CreateBrotli(), throwOnCompress: false, throwOnDecompress: true);
        await using var harness = await TestHarness.CreateAsync(
            runtimeConfigure: options =>
            {
                options.FlowControl.StreamReceiveWindowBytes = 64;
                options.FlowControl.ConnectionReceiveWindowBytes = 64;
            },
            clientRuntimeConfigure: options => options.Compression.Providers.Add(
                SharpLinkCompressionProviders.CreateBrotli()),
            serverRuntimeConfigure: options => options.Compression.Providers.Add(serverProvider));
        var payloads = Enumerable.Range(0, 256)
            .Select(static index => Enumerable.Repeat((byte)index, 128).ToArray());

        await harness.Client.Get<ICompressionService>()
            .NotifyStreamWithHeaderAsync(
                Enumerable.Repeat((byte)0x41, 4096).ToArray(),
                ToAsyncEnumerable(payloads, CancellationToken.None))
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        await Task.Delay(100);
        Ensure(!CompressionService.WaitForOneWayAsync().IsCompleted,
            "failed compressed OneWay request must not execute without admission");
        Ensure(await harness.Client.Get<ITestService>().AddAsync(20, 22) == 42,
            "non-admission compressed OneWay failure connection recovery");
    }

    [Test]
    [NotInParallel]
    public async Task QueuedOneWayStubFailureShouldDrainReservedStreams()
    {
        TestService.ResetMalformedOneWayInvocations();
        await using var harness = await TestHarness.CreateAsync(
            runtimeConfigure: options =>
            {
                options.FlowControl.StreamReceiveWindowBytes = 64;
                options.FlowControl.ConnectionReceiveWindowBytes = 64;
            },
            serverConfigure: builder =>
            {
                builder.UseCodec(new ThrowingPersonCodec());
                builder.UseAdmissionControl(options =>
                {
                    options.Global.UseConcurrency(1);
                    options.MaxQueuedCalls = 1;
                    options.MaxQueuedBytes = 64 * 1024;
                    options.MaxQueueDelay = TimeSpan.FromSeconds(2);
                    options.QueueOneWayCalls = true;
                });
            });
        var service = harness.Client.Get<ITestService>();
        var permitOwner = service.SlowAddWithoutTimeoutAsync(10, 11).AsTask();
        await Task.Delay(75);
        var failedOneWay = service.NotifyUploadWithHeaderAsync(
            new Person { Name = "malformed-oneway", Age = 1 },
            ToAsyncEnumerable(Enumerable.Range(1, 256), CancellationToken.None)).AsTask();

        Ensure(await permitOwner.WaitAsync(TimeSpan.FromSeconds(2)) == 21,
            "queued malformed OneWay permit owner");
        await failedOneWay.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        Ensure(TestService.MalformedOneWayInvocations == 0,
            "malformed OneWay request must not execute the service");
        Ensure(await service.AddAsync(20, 22) == 42,
            "malformed OneWay stub failure connection recovery");
    }

    [Test]
    [NotInParallel]
    public async Task ServerStreamConsumerExitShouldReleaseCallStreamAndAdmissionResources()
    {
        using var metrics = new LifecycleMetricProbe(
            LifecycleMetricProbe.ActiveCalls,
            LifecycleMetricProbe.ActiveStreams,
            LifecycleMetricProbe.AdmissionPermits);
        TestService.ResetDownloadDisposed();
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options => options.Global.UseConcurrency(1)));
        var client = (SharpLinkClient)harness.Client;
        var service = harness.Client.Get<ITestService>();
        await using (var enumerator = service.SlowDownloadAsync(
            1_000, 10, CancellationToken.None).GetAsyncEnumerator())
        {
            Ensure(await enumerator.MoveNextAsync(), "admitted server stream first item");
            await metrics.WaitForValueAsync(
                LifecycleMetricProbe.AdmissionPermits, 1, "P2-T06 admitted stream permit");
            await EnsureThrowsSharpLinkFast(
                service.AddAsync(1, 1).AsTask(),
                "permit held for server stream",
                SharpLinkErrorCode.ResourceExhausted);
        }

        await TestService.WaitForDownloadDisposedAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await metrics.WaitForValueAsync(
            LifecycleMetricProbe.AdmissionPermits, 0, "P2-T06 permit release");
        await metrics.WaitForValueAsync(
            LifecycleMetricProbe.ActiveStreams, 0, "P2-T06 stream release");
        await metrics.WaitForValueAsync(
            LifecycleMetricProbe.ActiveCalls, 0, "P2-T06 call release");
        Ensure(client.PendingCallCount == 0 && client.ActiveClientCallCount == 0 &&
               client.ActiveClientStreamCount == 0,
            "P2-T06 client pending/call/stream resources return to zero");
        Ensure(await service.AddAsync(20, 22) == 42,
            "P2-T06 permit is reusable immediately after the disposal gate");
        await StopHarnessAndAssertResourcesAsync(harness, "P2-T06 static");
    }

    [Test]
    [NotInParallel]
    public async Task ServerStopShouldCancelAdmissionWaitersWithoutUnboundedDelay()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 4096;
                options.MaxQueueDelay = TimeSpan.FromSeconds(10);
            }));
        var service = harness.Client.Get<ITestService>();
        TestService.ResetBlockingAdd();
        var active = service.BlockingAddAsync(1, 1).AsTask();
        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(2));
            var queued = service.SlowAddWithoutTimeoutAsync(2, 2).AsTask();
            await Task.Delay(50);
            Ensure(!queued.IsCompleted, "queued call must await admission before stop");

            var started = Stopwatch.GetTimestamp();
            await harness.DisposeServerOnlyAsync(TimeSpan.Zero).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2), "bounded stop with waiter");
            await EnsureThrows<SharpLinkException>(queued, "queued call stopped before execution");
            await EnsureThrows<SharpLinkException>(active, "active call disconnected by forced stop");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
        }
    }

    [Test]
    [NotInParallel]
    public async Task ClientDisconnectWhileAdmissionQueuedShouldReleaseWaiterAndAllConnections()
    {
        using var metrics = new LifecycleMetricProbe(
            LifecycleMetricProbe.ActiveConnections,
            LifecycleMetricProbe.AdmissionPermits,
            LifecycleMetricProbe.AdmissionQueuedCalls);
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1);
                options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 4096;
                options.MaxQueueDelay = TimeSpan.FromSeconds(10);
            }));
        var service = harness.Client.Get<ITestService>();
        TestService.ResetBlockingAdd();
        var active = service.BlockingAddAsync(1, 1, CancellationToken.None).AsTask();
        try
        {
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var queued = service.SlowAddWithoutTimeoutAsync(2, 2).AsTask();
            await metrics.WaitForValueAsync(
                LifecycleMetricProbe.AdmissionQueuedCalls, 1, "P2-T05 queued waiter");
            Ensure(!queued.IsCompleted, "queued call must await admission before disconnect");

            await harness.DisposeClientOnlyAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Ensure(await CaptureExceptionAsync(queued) is SharpLinkException,
                "P2-T05 disconnected admission waiter has one terminal error");
            Ensure(await CaptureExceptionAsync(active) is SharpLinkException,
                "P2-T05 disconnected active call has one terminal error");
            await metrics.WaitForValueAsync(
                LifecycleMetricProbe.AdmissionQueuedCalls, 0, "P2-T05 waiter release");
            await metrics.WaitForValueAsync(
                LifecycleMetricProbe.AdmissionPermits, 0, "P2-T05 permit release");
            await harness.DisposeServerOnlyAsync(TimeSpan.FromSeconds(1))
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await metrics.WaitForValueAsync(
                LifecycleMetricProbe.ActiveConnections, 0, "P2-T05 connection release");
            await StopHarnessAndAssertResourcesAsync(harness, "P2-T05");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
        }
    }

    private static async Task EnsureThrows<TException>(Task task, string name) where TException : Exception
    {
        try
        {
            await task;
            throw new Exception($"assert failed: {name} should throw {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }

    private static async Task<Exception?> CaptureExceptionAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task StopHarnessAndAssertResourcesAsync(TestHarness harness, string scenario)
    {
        await harness.DisposeClientOnlyAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await harness.DisposeServerOnlyAsync(TimeSpan.Zero).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await harness.WaitForServerExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var client = (SharpLinkClient)harness.Client;
        var server = ServerLifecycleResourceInspector.Capture(harness.Server);
        if (!ServerResourcesAreZero(server))
        {
            try
            {
                await WaitUntilAsync(() =>
                    ServerResourcesAreZero(ServerLifecycleResourceInspector.Capture(harness.Server)));
            }
            catch (OperationCanceledException)
            {
                // Preserve the strict assertion below so a timeout reports the final counters.
            }
            server = ServerLifecycleResourceInspector.Capture(harness.Server);
        }
        Ensure(harness.Client.State == SharpLinkConnectionState.Stopped,
            $"{scenario}: client stopped within the bound");
        Ensure(harness.Server.HealthStatus == SharpLinkHealthStatus.Unhealthy,
            $"{scenario}: server stopped within the bound");
        Ensure(client.PendingCallCount == 0 && client.ActiveClientCallCount == 0 &&
               client.ActiveClientStreamCount == 0,
            $"{scenario}: client pending/call/stream resources are zero");
        Ensure(server is
        {
            ActiveCalls: 0,
            Connections: 0,
            RetiredConnections: 0,
            AdmissionPermits: 0,
            AdmissionQueuedCalls: 0,
            AdmissionQueuedBytes: 0
        },
            $"{scenario}: server connection/call/admission resources are zero; actual {server}");
    }

    private static bool ServerResourcesAreZero(ServerLifecycleResourceSnapshot snapshot)
        => snapshot is
        {
            ActiveCalls: 0,
            Connections: 0,
            RetiredConnections: 0,
            AdmissionPermits: 0,
            AdmissionQueuedCalls: 0,
            AdmissionQueuedBytes: 0
        };

    private static async Task EnsureClientStreamProducerFailure(Task task, string name)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            throw new Exception($"assert failed: {name} should fail");
        }
        catch (InvalidOperationException)
        {
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.Internal)
        {
        }
    }

    private static async Task EnsureThrowsSharpLinkFast(
        Task task,
        string name,
        params SharpLinkErrorCode[] errorCodes)
    {
        Ensure(errorCodes.Length > 0, $"{name} expected error codes");
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            throw new Exception($"assert failed: {name} should throw");
        }
        catch (TimeoutException)
        {
            throw new Exception($"assert failed: {name} did not fail fast");
        }
        catch (SharpLinkException ex)
        {
            Ensure(
                errorCodes.Contains(ex.Code),
                $"{name} error code: expected {string.Join(" or ", errorCodes)}, actual {ex.Code}");
        }
    }

    private static void Ensure(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"assert failed: {name}");
    }

    private static Exception? CaptureException(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> stream, CancellationToken ct)
    {
        var list = new List<T>();
        await foreach (var item in stream.WithCancellation(ct))
            list.Add(item);
        return list;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(IEnumerable<T> values, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var value in values)
        {
            ct.ThrowIfCancellationRequested();
            yield return value;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<int> YieldOneThenWaitAsync(
        int value,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return value;
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static async IAsyncEnumerable<int> YieldThenFailAsync(int value)
    {
        yield return value;
        await Task.Yield();
        throw new InvalidOperationException("injected client stream producer failure");
    }

    private sealed class RecordingServerStreamExceptionMapper : IRpcExceptionMapper
    {
        private readonly Lock _gate = new();
        private readonly List<SharpLinkErrorCode?> _mappedCodes = [];

        public SharpLinkException Map(Exception exception, SharpLinkServerInvocationContext context)
        {
            if (context.Method.Kind == RpcMethodKind.ServerStreaming)
            {
                lock (_gate)
                    _mappedCodes.Add((exception as SharpLinkException)?.Code);
            }

            if (exception is SharpLinkException sharpLinkException)
                return sharpLinkException;
            return exception is OperationCanceledException
                ? new SharpLinkException(SharpLinkErrorCode.Cancelled, "The server call was cancelled.", exception)
                : new SharpLinkException(SharpLinkErrorCode.Internal, "Internal service error.", exception);
        }

        public SharpLinkErrorCode?[] GetMappedCodes()
        {
            lock (_gate)
                return [.. _mappedCodes];
        }
    }

    private sealed class SequencedTenantMetadataInterceptor : ISharpLinkClientInterceptor
    {
        private int _invocationCount;

        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            var invocation = Interlocked.Increment(ref _invocationCount);
            var tenant = invocation <= 2 ? "a" : "b";
            context.Metadata = new SharpLinkMetadata(
                new KeyValuePair<string, string>("tenant", tenant));
            return next(context);
        }
    }

    private sealed class TestHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;
        private bool _serverDisposed;
        private bool _clientDisposed;

        public ISharpLinkClient Client { get; }
        internal ISharpLinkServer Server => _server;

        private TestHarness(
            ISharpLinkServer server,
            Task serverTask,
            CancellationTokenSource serverCts,
            ISharpLinkClient client)
        {
            _server = server;
            _serverTask = serverTask;
            _serverCts = serverCts;
            Client = client;
        }

        public static async Task<TestHarness> CreateAsync(
            TimeSpan? requestTimeout = null,
            Func<Type, IRpcCodec?>? codecResolver = null,
            Action<SharpLinkRuntimeOptions>? runtimeConfigure = null,
            Action<SharpLinkConnectionPoolOptions>? poolConfigure = null,
            bool disableRequestTimeout = false,
            bool useSharedMemory = false,
            Action<SharpLinkRuntimeOptions>? serverRuntimeConfigure = null,
            Action<SharpLinkRuntimeOptions>? clientRuntimeConfigure = null,
            Action<SharpLinkServerBuilder>? serverConfigure = null,
            IRpcCodec<Person>? personCodec = null,
            ISharpLinkClientInterceptor? clientInterceptor = null)
        {
            var cts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));
            if (codecResolver is not null)
                serverBuilder.UseSerializer(codecResolver);
            if (personCodec is not null)
                serverBuilder.UseCodec(personCodec);
            if (runtimeConfigure is not null)
                serverBuilder.UseRuntime(runtimeConfigure);
            if (serverRuntimeConfigure is not null)
                serverBuilder.UseRuntime(serverRuntimeConfigure);
            serverConfigure?.Invoke(serverBuilder);

            var sharedMemoryName = $"sharplink-behavior-{Guid.NewGuid():N}";
            var port = 0;
            if (useSharedMemory)
                serverBuilder.UseSharedMemory(sharedMemoryName);
            else
            {
                serverBuilder.UseTcp(0, IPAddress.Loopback.ToString());
                port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            }
            var server = serverBuilder.Build();

            var serverTask = Task.Run(async () =>
            {
                try
                {
                    await server.RunAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                catch (IOException)
                {
                }
                catch (SocketException)
                {
                }
            }, CancellationToken.None);

            var clientBuilder = SharpClientBuilder.Create()
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));
            if (codecResolver is not null)
                clientBuilder.UseSerializer(codecResolver);
            if (personCodec is not null)
                clientBuilder.UseCodec(personCodec);
            if (useSharedMemory)
                clientBuilder.UseSharedMemory(sharedMemoryName);
            else
                clientBuilder.UseTcp(IPAddress.Loopback.ToString(), port);
            if (runtimeConfigure is not null)
                clientBuilder.UseRuntime(runtimeConfigure);
            if (clientRuntimeConfigure is not null)
                clientBuilder.UseRuntime(clientRuntimeConfigure);
            if (poolConfigure is not null)
                clientBuilder.UseConnectionPool(poolConfigure);
            if (clientInterceptor is not null)
                clientBuilder.AddInterceptor(clientInterceptor);

            if (disableRequestTimeout)
                clientBuilder.DisableRequestTimeout();
            else if (requestTimeout is { } timeout)
                clientBuilder.UseRequestTimeout(timeout);

            var client = clientBuilder.Build();
            await client.ConnectAsync();

            return new TestHarness(server, serverTask, cts, client);
        }

        public async ValueTask DisposeServerOnlyAsync(TimeSpan? gracefulTimeout = null)
        {
            if (_serverDisposed)
                return;

            _serverDisposed = true;
            await _server.StopAsync(gracefulTimeout ?? TimeSpan.Zero);
        }

        public async ValueTask DisposeClientOnlyAsync()
        {
            if (_clientDisposed)
                return;

            _clientDisposed = true;
            await Client.StopAsync();
        }

        internal Task WaitForServerExitAsync() => _serverTask;

        public async ValueTask DisposeAsync()
        {
            await DisposeClientOnlyAsync();
            await _serverCts.CancelAsync();
            await DisposeServerOnlyAsync();
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            _serverCts.Dispose();
        }
    }

    private sealed class CountingCompressionProvider(
        ISharpLinkCompressionProvider inner,
        string? wireProfile = null)
        : ISharpLinkCompressionProvider
    {
        private int _compressCount;
        private int _decompressCount;
        public string WireProfile => wireProfile ?? inner.WireProfile;
        public int CompressCount => Volatile.Read(ref _compressCount);
        public int DecompressCount => Volatile.Read(ref _decompressCount);

        public SharpLinkCompressionResult Compress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _compressCount);
            return inner.Compress(input, output, maxOutputBytes, cancellationToken);
        }

        public SharpLinkCompressionResult Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _decompressCount);
            return inner.Decompress(input, output, maxOutputBytes, cancellationToken);
        }
    }

    private sealed class NoBenefitCompressionProvider : ISharpLinkCompressionProvider
    {
        public string WireProfile => "test.identity/v1";

        public SharpLinkCompressionResult Compress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            foreach (var segment in input)
                output.Write(segment.Span);
            return new SharpLinkCompressionResult(
                checked((int)input.Length), checked((int)input.Length));
        }

        public SharpLinkCompressionResult Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Identity candidates must never be selected.");
    }

    private sealed class ThrowingCompressionProvider(
        ISharpLinkCompressionProvider inner,
        bool throwOnCompress,
        bool throwOnDecompress) : ISharpLinkCompressionProvider
    {
        public string WireProfile => inner.WireProfile;

        public SharpLinkCompressionResult Compress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => throwOnCompress
                ? throw new InvalidOperationException("Injected compression failure.")
                : inner.Compress(input, output, maxOutputBytes, cancellationToken);

        public SharpLinkCompressionResult Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => throwOnDecompress
                ? throw new InvalidOperationException("Injected decompression failure.")
                : inner.Decompress(input, output, maxOutputBytes, cancellationToken);
    }

    private sealed class ThrowingPersonCodec : IRpcCodec<Person>
    {
        public void Serialize(in Person value, IBufferWriter<byte> buffer)
            => throw new NotSupportedException("The server never serializes this request argument.");

        public Person? Deserialize(in ReadOnlySequence<byte> buffer)
            => throw new InvalidDataException("Injected request argument decode failure.");
    }

    private sealed class MarkerPersonCodec(byte marker) : IRpcCodec<Person>
    {
        private readonly byte _marker = marker;
        private readonly IRpcCodec<Person> _inner = SharpPackRpcCodec.Create<Person>(new SharpPackSerializerContext());
        public int SerializeCount;
        public int DeserializeCount;

        public void Serialize(in Person value, IBufferWriter<byte> buffer)
        {
            var markerSpan = buffer.GetSpan(1);
            markerSpan[0] = _marker;
            buffer.Advance(1);
            _inner.Serialize(value, buffer);
            Interlocked.Increment(ref SerializeCount);
        }

        public Person? Deserialize(in ReadOnlySequence<byte> buffer)
        {
            var reader = new SequenceReader<byte>(buffer);
            if (!reader.TryRead(out var actualMarker) || actualMarker != _marker)
                throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "DTO codec marker mismatch.");
            Interlocked.Increment(ref DeserializeCount);
            var payload = buffer.Slice(reader.Position);
            return _inner.Deserialize(payload);
        }
    }
}

[RpcContract]
public interface ITestService : IService
{
    [NonCancellable]
    ValueTask<int> AddAsync(int left, int right);
    [Sdk.Timeout]
    ValueTask<int> SlowAddAsync(int left, int right, CancellationToken cancellationToken);
    [NonCancellable]
    ValueTask<int> SlowAddWithoutTimeoutAsync(int left, int right);
    ValueTask<int> BlockingAddAsync(
        int left,
        int right,
        CancellationToken cancellationToken = default);
    [NonCancellable]
    ValueTask<int> SlowThrowWithoutTimeoutAsync();
    [NonCancellable]
    ValueTask ThrowCancellationAsync();
    [Sdk.Timeout(0.1)]
    ValueTask<int> SlowAddWithMethodTimeoutAsync(
        int left,
        int right,
        CancellationToken cancellationToken);
    [Sdk.Timeout(2)]
    ValueTask<string> DescribeCallAsync(
        int value,
        CancellationToken cancellationToken);
    [NonCancellable]
    ValueTask<Person> EchoAsync(Person person);
    [NonCancellable]
    ValueTask<GeneratedEnvelope> EchoGeneratedAsync(GeneratedEnvelope value);
    ValueTask<int> UploadAsync(
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken = default);
    [NonCancellable]
    ValueTask<int> UploadWithHeaderAsync(Person header, IAsyncEnumerable<int> values);
    [NonCancellable]
    IAsyncEnumerable<string> DownloadAsync(int count);
    IAsyncEnumerable<int> SlowDownloadAsync(int count, int delayMs, CancellationToken cancellationToken);
    [Oneway]
    [Sdk.Timeout(0.1)]
    ValueTask WaitForOneWayDeadlineAsync(CancellationToken cancellationToken);
    [Oneway]
    [NonCancellable]
    ValueTask NotifyAsync(string message);
    [Oneway]
    [NonCancellable]
    ValueTask NotifyUploadWithHeaderAsync(Person header, IAsyncEnumerable<int> values);
}

[RpcService]
public class TestService : ITestService
{
    private static TaskCompletionSource s_nonCancellableCompletion = CreateCompletionSource();
    private static TaskCompletionSource s_nonCancellableFailure = CreateCompletionSource();
    private static TaskCompletionSource s_blockingAddStarted = CreateCompletionSource();
    private static TaskCompletionSource s_blockingAddRelease = CreateCompletionSource();
    private static TaskCompletionSource s_downloadDisposed = CreateCompletionSource();
    private static int s_blockingAddExpectedStarts = 1;
    private static int s_blockingAddStartedCount;
    private static int s_activeUploads;
    private static int s_malformedUploadInvocations;
    private static int s_malformedOneWayInvocations;
    private static int s_notifyCount;
    private static TaskCompletionSource s_notify = CreateCompletionSource();
    private static TaskCompletionSource s_oneWayDeadlineCancellation = CreateCompletionSource();

    internal static int ActiveUploads => Volatile.Read(ref s_activeUploads);
    internal static int MalformedUploadInvocations => Volatile.Read(ref s_malformedUploadInvocations);
    internal static int MalformedOneWayInvocations => Volatile.Read(ref s_malformedOneWayInvocations);
    internal static int NotifyCount => Volatile.Read(ref s_notifyCount);

    internal static void ResetNotify()
    {
        Volatile.Write(ref s_notifyCount, 0);
        Interlocked.Exchange(ref s_notify, CreateCompletionSource());
    }

    internal static void ResetMalformedOneWayInvocations()
        => Volatile.Write(ref s_malformedOneWayInvocations, 0);

    internal static Task WaitForNotifyAsync() => Volatile.Read(ref s_notify).Task;

    internal static void ResetOneWayDeadlineCancellation()
        => Interlocked.Exchange(ref s_oneWayDeadlineCancellation, CreateCompletionSource());

    internal static Task WaitForOneWayDeadlineCancellationAsync()
        => Volatile.Read(ref s_oneWayDeadlineCancellation).Task;

    internal static void ResetActiveUploads() => Volatile.Write(ref s_activeUploads, 0);
    internal static void ResetMalformedUploadInvocations()
        => Volatile.Write(ref s_malformedUploadInvocations, 0);

    internal static void ResetNonCancellableCompletion()
        => Interlocked.Exchange(ref s_nonCancellableCompletion, CreateCompletionSource());

    internal static Task WaitForNonCancellableCompletionAsync()
        => Volatile.Read(ref s_nonCancellableCompletion).Task;

    internal static void ResetNonCancellableFailure()
        => Interlocked.Exchange(ref s_nonCancellableFailure, CreateCompletionSource());

    internal static Task WaitForNonCancellableFailureAsync()
        => Volatile.Read(ref s_nonCancellableFailure).Task;

    internal static void ResetBlockingAdd(int expectedStarts = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedStarts);
        Volatile.Write(ref s_blockingAddExpectedStarts, expectedStarts);
        Volatile.Write(ref s_blockingAddStartedCount, 0);
        Interlocked.Exchange(ref s_blockingAddStarted, CreateCompletionSource());
        Interlocked.Exchange(ref s_blockingAddRelease, CreateCompletionSource());
    }

    internal static Task WaitForBlockingAddStartedAsync()
        => Volatile.Read(ref s_blockingAddStarted).Task;

    internal static void ReleaseBlockingAdd()
        => Volatile.Read(ref s_blockingAddRelease).TrySetResult();

    internal static void ResetDownloadDisposed()
        => Interlocked.Exchange(ref s_downloadDisposed, CreateCompletionSource());

    internal static Task WaitForDownloadDisposedAsync()
        => Volatile.Read(ref s_downloadDisposed).Task;

    public ValueTask<int> AddAsync(int left, int right) => ValueTask.FromResult(left + right);

    public async ValueTask<int> SlowAddAsync(int left, int right, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        return left + right;
    }

    public async ValueTask<int> SlowAddWithoutTimeoutAsync(int left, int right)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Volatile.Read(ref s_nonCancellableCompletion).TrySetResult();
        return left + right;
    }

    public async ValueTask<int> BlockingAddAsync(
        int left,
        int right,
        CancellationToken cancellationToken = default)
    {
        var release = Volatile.Read(ref s_blockingAddRelease);
        if (Interlocked.Increment(ref s_blockingAddStartedCount) ==
            Volatile.Read(ref s_blockingAddExpectedStarts))
        {
            Volatile.Read(ref s_blockingAddStarted).TrySetResult();
        }
        await release.Task.WaitAsync(cancellationToken);
        return left + right;
    }

    public async ValueTask<int> SlowThrowWithoutTimeoutAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Volatile.Read(ref s_nonCancellableFailure).TrySetResult();
        throw new InvalidOperationException("late non-cancellable failure");
    }

    public ValueTask ThrowCancellationAsync()
        => ValueTask.FromException(new OperationCanceledException("service-specific cancellation"));

    public async ValueTask<int> SlowAddWithMethodTimeoutAsync(
        int left,
        int right,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        return left + right;
    }

    public ValueTask<string> DescribeCallAsync(
        int value,
        CancellationToken cancellationToken)
    {
        var context = SharpLinkCallContext.Current;
        var tenant = context?.Metadata is { Count: > 0 } metadata
            ? metadata[0].Value
            : "missing";
        const string deadline = "no-deadline";
        return ValueTask.FromResult($"{value}:{tenant}:{deadline}");
    }

    public ValueTask<Person> EchoAsync(Person person)
    {
        person.Name += "-r";
        person.Age += 1;
        return ValueTask.FromResult(person);
    }

    public ValueTask<GeneratedEnvelope> EchoGeneratedAsync(GeneratedEnvelope value)
        => ValueTask.FromResult(value with { Name = value.Name + "-r", Age = value.Age + 1 });

    public async ValueTask<int> UploadAsync(
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref s_activeUploads);
        try
        {
            var sum = 0;
            await foreach (var i in values.WithCancellation(cancellationToken))
                sum += i;
            return sum;
        }
        finally
        {
            Interlocked.Decrement(ref s_activeUploads);
        }
    }

    public async ValueTask<int> UploadWithHeaderAsync(
        Person header,
        IAsyncEnumerable<int> values)
    {
        _ = header;
        Interlocked.Increment(ref s_malformedUploadInvocations);
        var sum = 0;
        await foreach (var value in values)
            sum += value;
        return sum;
    }

    public async ValueTask NotifyUploadWithHeaderAsync(
        Person header,
        IAsyncEnumerable<int> values)
    {
        _ = header;
        Interlocked.Increment(ref s_malformedOneWayInvocations);
        await foreach (var value in values)
            _ = value;
    }

    public async IAsyncEnumerable<string> DownloadAsync(int count)
    {
        try
        {
            for (var i = 0; i < count; i++)
            {
                yield return $"v-{i}";
                await Task.Yield();
            }
        }
        finally
        {
            Volatile.Read(ref s_downloadDisposed).TrySetResult();
        }
    }

    public async IAsyncEnumerable<int> SlowDownloadAsync(int count, int delayMs, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return i;
                await Task.Delay(delayMs, cancellationToken);
            }
        }
        finally
        {
            Volatile.Read(ref s_downloadDisposed).TrySetResult();
        }
    }

    public async ValueTask WaitForOneWayDeadlineAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Volatile.Read(ref s_oneWayDeadlineCancellation).TrySetResult();
        }
    }

    public ValueTask NotifyAsync(string message)
    {
        _ = message;
        Interlocked.Increment(ref s_notifyCount);
        Volatile.Read(ref s_notify).TrySetResult();
        return ValueTask.CompletedTask;
    }

    private static TaskCompletionSource CreateCompletionSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

[RpcContract]
public interface ICompressionService : IService
{
    [NonCancellable]
    ValueTask<byte[]> EchoBytesAsync(byte[] value);

    [Oneway]
    [NonCancellable]
    ValueTask NotifyBytesAsync(byte[] value);

    [Oneway]
    [NonCancellable]
    ValueTask NotifyStreamBytesAsync(IAsyncEnumerable<byte[]> values);

    [Oneway]
    [NonCancellable]
    ValueTask NotifyStreamWithHeaderAsync(byte[] header, IAsyncEnumerable<byte[]> values);

    [NonCancellable]
    ValueTask<int> UploadBytesAsync(IAsyncEnumerable<byte[]> values);

    [NonCancellable]
    IAsyncEnumerable<byte[]> DownloadBytesAsync(int count, int size);

    [NonCancellable]
    IAsyncEnumerable<byte[]> DuplexBytesAsync(IAsyncEnumerable<byte[]> values);
}

[RpcService]
public sealed class CompressionService : ICompressionService
{
    private static TaskCompletionSource<int> s_oneWay = CreateOneWayCompletion();

    internal static void ResetOneWay()
        => Interlocked.Exchange(ref s_oneWay, CreateOneWayCompletion());

    internal static Task<int> WaitForOneWayAsync() => Volatile.Read(ref s_oneWay).Task;

    public ValueTask<byte[]> EchoBytesAsync(byte[] value) => ValueTask.FromResult(value);

    public ValueTask NotifyBytesAsync(byte[] value)
    {
        Volatile.Read(ref s_oneWay).TrySetResult(value.Length);
        return ValueTask.CompletedTask;
    }

    public async ValueTask NotifyStreamBytesAsync(IAsyncEnumerable<byte[]> values)
    {
        var total = 0;
        await foreach (var value in values)
            total += value.Length;
        Volatile.Read(ref s_oneWay).TrySetResult(total);
    }

    public async ValueTask NotifyStreamWithHeaderAsync(
        byte[] header,
        IAsyncEnumerable<byte[]> values)
    {
        var total = header.Length;
        await foreach (var value in values)
            total += value.Length;
        Volatile.Read(ref s_oneWay).TrySetResult(total);
    }

    public async ValueTask<int> UploadBytesAsync(IAsyncEnumerable<byte[]> values)
    {
        var total = 0;
        await foreach (var value in values)
            total += value.Length;
        return total;
    }

    public async IAsyncEnumerable<byte[]> DownloadBytesAsync(int count, int size)
    {
        var payload = Enumerable.Repeat((byte)0x2a, size).ToArray();
        for (var index = 0; index < count; index++)
        {
            yield return payload;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<byte[]> DuplexBytesAsync(IAsyncEnumerable<byte[]> values)
    {
        await foreach (var value in values)
            yield return value;
    }

    private static TaskCompletionSource<int> CreateOneWayCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

[SharpPackable]
public partial class Person
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public List<string> Tags { get; set; } = [];
}

public sealed record GeneratedAddress(
    [property: RpcMember(1)] string City);

public sealed record GeneratedEnvelope(
    [property: RpcRequired] string Name,
    int Age,
    GeneratedAddress Address,
    List<string> Tags);

[RpcContract]
public interface IGeneratedSemanticContract : IService
{
    [NonCancellable]
    ValueTask<GeneratedSemanticEnvelope> EchoAsync(GeneratedSemanticEnvelope value);
}

public sealed record GeneratedSemanticEnvelope(
    [property: RpcMember(1)] bool Boolean,
    [property: RpcMember(2)] System.Text.Rune Rune,
    [property: RpcMember(3)] decimal Decimal,
    [property: RpcMember(4)] DateOnly DateOnly,
    [property: RpcMember(5)] DateTime DateTime,
    [property: RpcMember(6)] TimeOnly TimeOnly,
    [property: RpcMember(7)] DateTimeOffset DateTimeOffset);
