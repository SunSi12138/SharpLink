namespace SharpLink.IntegrationTests;

public class CompressionCapacityFullMalformedMetadataTests
{
    [Test]
    [NotInParallel]
    public async Task CapacityFullMalformedCompressedUnaryMetadataShouldStillCloseConnection()
    {
        await VerifyCapacityFullMalformedMetadataClosesConnectionAsync(
            methodName: nameof(ICompressionOwnershipProbeService.BlockAfterDecodeAsync),
            flags: ProtocolV2FrameFlags.Compressed |
                   ProtocolV2FrameFlags.HasMetadata |
                   ProtocolV2FrameFlags.Cancellable |
                   ProtocolV2FrameFlags.HasReturn);
    }

    [Test]
    [NotInParallel]
    public async Task CapacityFullMalformedCompressedOneWayMetadataShouldStillCloseConnection()
    {
        await VerifyCapacityFullMalformedMetadataClosesConnectionAsync(
            methodName: nameof(ICompressionOwnershipProbeService.CancellableNotifyAsync),
            flags: ProtocolV2FrameFlags.Compressed |
                   ProtocolV2FrameFlags.HasMetadata |
                   ProtocolV2FrameFlags.Cancellable |
                   ProtocolV2FrameFlags.OneWay);
    }

    private static async Task VerifyCapacityFullMalformedMetadataClosesConnectionAsync(
        string methodName,
        ProtocolV2FrameFlags flags)
    {
        TestService.ResetBlockingAdd();
        CompressionOwnershipProbeService.Reset();
        var provider = new CountingProvider(SharpLinkCompressionProviders.CreateBrotli());
        using var serverCts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseHeartbeat(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30))
            .UseRuntime(options =>
            {
                options.FlowControl.MaxConcurrentCallsPerConnection = 1;
                options.FlowControl.MaxConcurrentCallsPerServer = 1;
                options.Compression.Providers.Add(provider);
            })
            .UseTcp(0, IPAddress.Loopback.ToString());
        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(serverCts.Token);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
            {
            }
        }, CancellationToken.None);

        var client = SharpClientBuilder.Create()
            .UseHeartbeat(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30))
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseRuntime(options => options.Compression.Providers.Add(
                SharpLinkCompressionProviders.CreateBrotli()))
            .Build();

        Task<int>? blocker = null;
        try
        {
            await client.ConnectAsync();
            blocker = client.Get<ITestService>()
                .BlockingAddAsync(10, 20, CancellationToken.None)
                .AsTask();
            await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(2));

            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, port);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var limits = new SharpLinkProtocolOptions();

            using (var handshake = new PooledByteBufferWriter())
            {
                var token = ProtocolV2FrameWriter.BeginFrame(
                    handshake,
                    ProtocolV2FrameType.HandshakeRequest,
                    ProtocolV2FrameFlags.None,
                    0);
                ProtocolV2PayloadCodec.WriteHandshakeRequest(
                    handshake,
                    new ProtocolV2HandshakeRequest(
                        ProtocolV2Constants.MinorVersion,
                        ProtocolV2Capabilities.Metadata |
                        ProtocolV2Capabilities.Compression |
                        ProtocolV2Capabilities.CancellationReason,
                        ProtocolV2Capabilities.Metadata |
                        ProtocolV2Capabilities.Compression,
                        SharpLinkProtocolOptions.DefaultMaxFramePayloadBytes,
                        1024 * 1024,
                        16 * 1024 * 1024,
                        ReadOnlyMemory<byte>.Empty,
                        new[] { provider.WireProfile }),
                    limits);
                ProtocolV2FrameWriter.EndFrame(handshake, token);
                await stream.WriteAsync(handshake.WrittenMemory);
                await stream.FlushAsync();
            }

            var handshakeResponse = await ReadFrameAsync(stream, limits);
            Ensure(handshakeResponse.Header.Type == ProtocolV2FrameType.HandshakeResponse,
                "capacity-full malformed metadata handshake response type");

            using (var request = new PooledByteBufferWriter())
            {
                var token = ProtocolV2FrameWriter.BeginFrame(
                    request,
                    ProtocolV2FrameType.Request,
                    flags,
                    1);
                WriteInt64(request, GetInterfaceHash(
                    "SharpLink.IntegrationTests.ICompressionOwnershipProbeService"));
                WriteInt64(request, GetMethodHash(methodName, "System.Byte[]"));

                // Length-valid metadata with invalid UTF-8. This must remain a connection-level
                // protocol violation even while global call capacity is exhausted.
                ProtocolV2PayloadCodec.WriteVarUInt32(request, 4);
                request.Write(new byte[] { 1, 1, 0xFF, 0 });

                var originalLength = request.GetSpan(sizeof(uint));
                BinaryPrimitives.WriteUInt32LittleEndian(originalLength, 1);
                request.Advance(sizeof(uint));
                request.Write(new byte[] { 0x00 });
                ProtocolV2FrameWriter.EndFrame(request, token);
                await stream.WriteAsync(request.WrittenMemory);
                await stream.FlushAsync();
            }

            await EnsureConnectionClosesAsync(stream);
            Ensure(provider.DecompressCount == 0,
                "capacity-full malformed compressed metadata must fail before decompression");
            Ensure(CompressionOwnershipProbeService.NotifyInvocations == 0,
                "capacity-full malformed compressed metadata must not invoke the service");
        }
        finally
        {
            TestService.ReleaseBlockingAdd();
            CompressionOwnershipProbeService.ReleaseBlock();
            if (blocker is not null)
            {
                try
                {
                    Ensure(await blocker.WaitAsync(TimeSpan.FromSeconds(2)) == 30,
                        "capacity blocker should complete after release");
                }
                catch (SharpLinkException exception) when (
                    exception.Code is SharpLinkErrorCode.ConnectionClosed or SharpLinkErrorCode.Unavailable)
                {
                }
            }
            await client.StopAsync();
            await serverCts.CancelAsync();
            await server.DisposeAsync();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    private static async Task<(ProtocolV2FrameHeader Header, byte[] Payload)> ReadFrameAsync(
        NetworkStream stream,
        SharpLinkProtocolOptions limits)
    {
        var bytes = new byte[256];
        var written = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (true)
        {
            var sequence = new ReadOnlySequence<byte>(bytes.AsMemory(0, written));
            if (ProtocolV2FrameParser.TryReadFrame(
                    ref sequence,
                    limits,
                    out var header,
                    out var payload))
            {
                return (header, payload.ToArray());
            }

            if (written == bytes.Length)
                Array.Resize(ref bytes, checked(bytes.Length * 2));
            var read = await stream.ReadAsync(bytes.AsMemory(written), timeout.Token);
            Ensure(read > 0, "connection closed before expected frame");
            written += read;
        }
    }

    private static async Task EnsureConnectionClosesAsync(NetworkStream stream)
    {
        var bytes = new byte[256];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            while (await stream.ReadAsync(bytes, timeout.Token) != 0)
            {
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new Exception("assert failed: capacity-full malformed metadata did not close the connection");
        }
        catch (IOException)
        {
        }
    }

    private static long GetInterfaceHash(string interfaceName) => unchecked((long)Fnv1A(interfaceName));

    private static long GetMethodHash(string methodName, params string[] parameterTypes)
        => unchecked((long)Fnv1A($"{methodName}({string.Join(",", parameterTypes)})"));

    private static ulong Fnv1A(string value)
    {
        const ulong prime = 1099511628211;
        var hash = 14695981039346656037UL;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= prime;
        }
        return hash;
    }

    private static void WriteInt64(IBufferWriter<byte> writer, long value)
    {
        var span = writer.GetSpan(sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(span, value);
        writer.Advance(sizeof(long));
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }

    private sealed class CountingProvider(ISharpLinkCompressionProvider inner)
        : ISharpLinkCompressionProvider
    {
        private int _decompressCount;

        public string WireProfile => inner.WireProfile;
        public int DecompressCount => Volatile.Read(ref _decompressCount);

        public SharpLinkCompressionResult Compress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => inner.Compress(input, output, maxOutputBytes, cancellationToken);

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
}
