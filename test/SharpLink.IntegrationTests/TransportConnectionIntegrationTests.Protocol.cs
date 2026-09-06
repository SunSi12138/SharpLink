namespace SharpLink.IntegrationTests;

public partial class TransportConnectionIntegrationTests
{
    [Test]
    public async Task TcpServerShouldProcessRequestCoalescedWithHandshake()
    {
        using var serverCts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())
            ;
        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        var serverTask = server.RunAsync(serverCts.Token).AsTask();

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, port);
            await using var stream = new NetworkStream(socket, ownsSocket: false);

            using var frames = new PooledByteBufferWriter();
            var limits = new SharpLinkProtocolOptions();
            var handshakeToken = ProtocolV2FrameWriter.BeginFrame(
                frames,
                ProtocolV2FrameType.HandshakeRequest,
                ProtocolV2FrameFlags.None,
                0);
            ProtocolV2PayloadCodec.WriteHandshakeRequest(
                frames,
                new ProtocolV2HandshakeRequest(
                    ProtocolV2Constants.MinorVersion,
                    ProtocolV2Capabilities.HealthCheck,
                    ProtocolV2Capabilities.None,
                    SharpLinkProtocolOptions.DefaultMaxFramePayloadBytes,
                    1024 * 1024,
                    16 * 1024 * 1024,
                    ReadOnlyMemory<byte>.Empty),
                limits);
            ProtocolV2FrameWriter.EndFrame(frames, handshakeToken);
            var healthToken = ProtocolV2FrameWriter.BeginFrame(
                frames,
                ProtocolV2FrameType.HealthCheck,
                ProtocolV2FrameFlags.None,
                1);
            ProtocolV2FrameWriter.EndFrame(frames, healthToken);

            // One write makes the server handshake and first request share the same pipe read.
            await stream.WriteAsync(frames.WrittenMemory);
            await stream.FlushAsync();

            var received = new byte[4096];
            var receivedCount = 0;
            var consumedCount = 0;
            var responseCount = 0;
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (responseCount < 2)
            {
                var bytesRead = await stream.ReadAsync(received.AsMemory(receivedCount), readCts.Token);
                Ensure(bytesRead > 0, "server should return both coalesced-frame responses");
                receivedCount += bytesRead;
                var sequence = new ReadOnlySequence<byte>(
                    received.AsMemory(consumedCount, receivedCount - consumedCount));
                while (ProtocolV2FrameParser.TryReadFrame(
                           ref sequence,
                           limits,
                           out var header,
                           out var payload))
                {
                    if (responseCount == 0)
                        Ensure(header.Type == ProtocolV2FrameType.HandshakeResponse, "handshake response order");
                    else
                    {
                        Ensure(header.Type == ProtocolV2FrameType.HealthResponse, "coalesced health response type");
                        Ensure(header.RequestId == 1, "coalesced health response request ID");
                        Ensure(
                            ProtocolV2PayloadCodec.ReadHealthResponse(payload).Status == SharpLinkHealthStatus.Ready,
                            "coalesced health response status");
                    }
                    responseCount++;
                }
                consumedCount = receivedCount - checked((int)sequence.Length);
            }
        }
        finally
        {
            await serverCts.CancelAsync();
            await server.DisposeAsync();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Test]
    public async Task TcpServerShouldRejectLegacyProtocolMinorBeforeRpcTraffic()
    {
        using var serverCts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString());
        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        var serverTask = server.RunAsync(serverCts.Token).AsTask();

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, port);
            await using var stream = new NetworkStream(socket, ownsSocket: false);

            using var frame = new PooledByteBufferWriter();
            var limits = new SharpLinkProtocolOptions();
            var token = ProtocolV2FrameWriter.BeginFrame(
                frame,
                ProtocolV2FrameType.HandshakeRequest,
                ProtocolV2FrameFlags.None,
                0);
            ProtocolV2PayloadCodec.WriteHandshakeRequest(
                frame,
                new ProtocolV2HandshakeRequest(
                    checked((ushort)(ProtocolV2Constants.MinimumCompatibleMinorVersion - 1)),
                    ProtocolV2Capabilities.None,
                    ProtocolV2Capabilities.None,
                    SharpLinkProtocolOptions.DefaultMaxFramePayloadBytes,
                    1024 * 1024,
                    16 * 1024 * 1024,
                    ReadOnlyMemory<byte>.Empty),
                limits);
            ProtocolV2FrameWriter.EndFrame(frame, token);
            await stream.WriteAsync(frame.WrittenMemory);
            await stream.FlushAsync();

            var received = new byte[4096];
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var count = await stream.ReadAsync(received, readCts.Token);
            Ensure(count > 0, "legacy peer should receive an explicit handshake rejection");
            var sequence = new ReadOnlySequence<byte>(received.AsMemory(0, count));
            Ensure(ProtocolV2FrameParser.TryReadFrame(
                ref sequence,
                limits,
                out var header,
                out var payload),
                "legacy handshake rejection frame");
            Ensure(header.Type == ProtocolV2FrameType.HandshakeResponse, "legacy handshake response type");
            Ensure((header.Flags & ProtocolV2FrameFlags.Error) != 0, "legacy handshake must be rejected");
            var error = ProtocolV2PayloadCodec.ReadError(
                payload,
                header.Flags,
                limits.MaxErrorMessageBytes);
            Ensure(error.Code == SharpLinkErrorCode.Unimplemented,
                "pre-TimeBudget protocol minor should be rejected as incompatible");
        }
        finally
        {
            await serverCts.CancelAsync();
            await server.DisposeAsync();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Test]
    [NotInParallel]
    public async Task ServerProtocolViolationShouldReleaseItsReadBeforeCompletingTheReader()
    {
        using var serverCts = new CancellationTokenSource();
        var connection = new CompletionJoiningTransportConnection();
        var listener = new SingleConnectionListener(connection);
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTransport(listener);
        var server = serverBuilder.Build();
        var serverTask = server.RunAsync(serverCts.Token).AsTask();

        try
        {
            using var frames = new PooledByteBufferWriter();
            var limits = new SharpLinkProtocolOptions();
            var handshake = ProtocolV2FrameWriter.BeginFrame(
                frames,
                ProtocolV2FrameType.HandshakeRequest,
                ProtocolV2FrameFlags.None,
                0);
            ProtocolV2PayloadCodec.WriteHandshakeRequest(
                frames,
                new ProtocolV2HandshakeRequest(
                    ProtocolV2Constants.MinorVersion,
                    ProtocolV2Capabilities.None,
                    ProtocolV2Capabilities.None,
                    SharpLinkProtocolOptions.DefaultMaxFramePayloadBytes,
                    1024 * 1024,
                    16 * 1024 * 1024,
                    ReadOnlyMemory<byte>.Empty),
                limits);
            ProtocolV2FrameWriter.EndFrame(frames, handshake);

            var illegalResponse = ProtocolV2FrameWriter.BeginFrame(
                frames,
                ProtocolV2FrameType.Response,
                ProtocolV2FrameFlags.None,
                1);
            ProtocolV2FrameWriter.EndFrame(frames, illegalResponse);
            await connection.InjectAsync(frames.WrittenMemory);

            await connection.Reader.CompleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(!connection.Reader.CompleteObservedOutstandingRead,
                "terminal protocol teardown must AdvanceTo before awaiting reader completion");
        }
        finally
        {
            connection.Reader.ReleaseCompletion();
            await serverCts.CancelAsync();
            await server.DisposeAsync();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Test]
    [NotInParallel]
    public async Task ServerMalformedHandshakeShouldReleaseItsReadBeforeCompletingTheReader()
    {
        using var serverCts = new CancellationTokenSource();
        var connection = new CompletionJoiningTransportConnection();
        var listener = new SingleConnectionListener(connection);
        var server = SharpLinkServerBuilder.Create()
            .UseTransport(listener)
            .Build();
        var serverTask = server.RunAsync(serverCts.Token).AsTask();

        try
        {
            using var frame = new PooledByteBufferWriter();
            var token = ProtocolV2FrameWriter.BeginFrame(
                frame,
                ProtocolV2FrameType.HandshakeRequest,
                ProtocolV2FrameFlags.None,
                0);
            frame.Write(new byte[32]);
            ProtocolV2FrameWriter.EndFrame(frame, token);
            await connection.InjectAsync(frame.WrittenMemory);

            await connection.Reader.CompleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(!connection.Reader.CompleteObservedOutstandingRead,
                "malformed server handshake teardown must AdvanceTo before awaiting reader completion");
        }
        finally
        {
            connection.Reader.ReleaseCompletion();
            await serverCts.CancelAsync();
            await server.DisposeAsync();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Test]
    [NotInParallel]
    public async Task ClientMalformedHandshakeShouldReleaseItsReadBeforeCompletingTheReader()
    {
        var connection = new CompletionJoiningTransportConnection();
        var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTransport(new SingleConnectionClientFactory(connection))
            .Build();

        try
        {
            using var frame = new PooledByteBufferWriter();
            var token = ProtocolV2FrameWriter.BeginFrame(
                frame,
                ProtocolV2FrameType.HandshakeResponse,
                ProtocolV2FrameFlags.None,
                0);
            frame.Write(new byte[23]);
            ProtocolV2FrameWriter.EndFrame(frame, token);
            await connection.InjectAsync(frame.WrittenMemory);

            var connectTask = client.ConnectAsync().AsTask();
            await connection.Reader.CompleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(!connection.Reader.CompleteObservedOutstandingRead,
                "malformed client handshake teardown must AdvanceTo before awaiting reader completion");
            connection.Reader.ReleaseCompletion();
            await EnsureThrows<SharpLinkException>(connectTask, "malformed client handshake");
        }
        finally
        {
            connection.Reader.ReleaseCompletion();
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task TcpShouldEnforceNegotiatedFrameLimitInBothDirections()
    {
        await VerifyNegotiatedFrameLimitAsync(
            SharpLinkProtocolOptions.DefaultMaxFramePayloadBytes,
            SharpLinkProtocolOptions.MinMaxFramePayloadBytes);
        await VerifyNegotiatedFrameLimitAsync(
            SharpLinkProtocolOptions.MinMaxFramePayloadBytes,
            SharpLinkProtocolOptions.DefaultMaxFramePayloadBytes);
    }

    [Test]
    public async Task TcpClientHandshakeShouldHonorConfiguredTimeout()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var silentServerCts = new CancellationTokenSource();
        var silentServer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync(silentServerCts.Token);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, silentServerCts.Token);
            }
            catch (OperationCanceledException) when (silentServerCts.IsCancellationRequested)
            {
            }
        }, CancellationToken.None);
        var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), port)

            .UseProtocol(static options => options.HandshakeTimeout = TimeSpan.FromMilliseconds(120))
            .Build();

        try
        {
            await EnsureThrowsSharpLink(
                client.ConnectAsync(),
                "client handshake timeout",
                SharpLinkErrorCode.Unavailable,
                "timed out");
        }
        finally
        {
            if (client is IAsyncDisposable asyncClient)
                await asyncClient.DisposeAsync();
            await silentServerCts.CancelAsync();
            listener.Stop();
            await silentServer.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Test]
    public async Task TcpClientHandshakeShouldHonorCallerCancellation()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var silentServerCts = new CancellationTokenSource();
        var silentServer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync(silentServerCts.Token);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, silentServerCts.Token);
            }
            catch (OperationCanceledException) when (silentServerCts.IsCancellationRequested)
            {
            }
        }, CancellationToken.None);
        var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), port)

            .UseProtocol(static options => options.HandshakeTimeout = TimeSpan.FromSeconds(5))
            .Build();

        try
        {
            using var connectCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
            await EnsureThrows<OperationCanceledException>(
                client.ConnectAsync(connectCts.Token),
                "client cancellation during handshake");
        }
        finally
        {
            if (client is IAsyncDisposable asyncClient)
                await asyncClient.DisposeAsync();
            await silentServerCts.CancelAsync();
            listener.Stop();
            await silentServer.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Test]
    public async Task TcpServerShouldCloseSessionWhenClientNeverSendsHandshake()
    {
        using var serverCts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())

            .UseProtocol(static options => options.HandshakeTimeout = TimeSpan.FromMilliseconds(120));
        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        var serverTask = server.RunAsync(serverCts.Token).AsTask();

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, port);
            var buffer = new byte[1];
            var bytesRead = await socket.ReceiveAsync(buffer, SocketFlags.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(bytesRead == 0, "server should close a session that never handshakes");
        }
        finally
        {
            await serverCts.CancelAsync();
            if (server is IAsyncDisposable asyncServer)
                await asyncServer.DisposeAsync();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Test]
    public async Task TcpHandshakeFailureShouldReturnFalse()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var fakeServerTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync();
            await using var stream = new NetworkStream(socket, ownsSocket: true);
            using var writer = new PooledByteBufferWriter();
            using (writer.BeginPacketScope(
                       ProtocolV2FrameType.HandshakeResponse,
                       ProtocolV2FrameFlags.Error,
                       0))
            {
                ProtocolV2PayloadCodec.WriteError(
                    writer,
                    SharpLinkErrorCode.AuthenticationRejected,
                    "token rejected",
                    SharpLinkProtocolOptions.DefaultMaxErrorMessageBytes,
                    out _);
            }

            await stream.WriteAsync(writer.WrittenMemory);
            await stream.FlushAsync();
        });

        var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), port)

            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            var exception = await CaptureSharpLinkException(
                client.ConnectAsync().AsTask(),
                "tcp handshake rejection");
            Ensure(exception.Code == SharpLinkErrorCode.AuthenticationRejected, "handshake rejection code");
            Ensure(exception.Message == "token rejected", "handshake rejection message");
        }
        finally
        {
            await client.DisposeAsync();
            listener.Stop();
            await Task.WhenAny(fakeServerTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    [Test]
    public async Task TcpUnsupportedRequiredCapabilityShouldReturnUnimplementedAndClose()
    {
        using var serverCts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())
            ;
        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        var serverTask = server.RunAsync(serverCts.Token).AsTask();

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, port);
            await using var stream = new NetworkStream(socket, ownsSocket: false);

            var requestWriter = new PooledByteBufferWriter();
            var requestToken = ProtocolV2FrameWriter.BeginFrame(
                requestWriter,
                ProtocolV2FrameType.HandshakeRequest,
                ProtocolV2FrameFlags.None,
                0);
            ProtocolV2PayloadCodec.WriteHandshakeRequest(
                requestWriter,
                new ProtocolV2HandshakeRequest(
                    ProtocolV2Constants.MinorVersion,
                    (ProtocolV2Capabilities)(1UL << 63),
                    (ProtocolV2Capabilities)(1UL << 63),
                    SharpLinkProtocolOptions.DefaultMaxFramePayloadBytes,
                    1024 * 1024,
                    16 * 1024 * 1024,
                    ReadOnlyMemory<byte>.Empty),
                new SharpLinkProtocolOptions());
            ProtocolV2FrameWriter.EndFrame(requestWriter, requestToken);
            await stream.WriteAsync(requestWriter.WrittenMemory);
            await stream.FlushAsync();

            var received = new byte[1024];
            var receivedCount = 0;
            ProtocolV2FrameHeader responseHeader = default;
            ReadOnlySequence<byte> responsePayload = default;
            var parsed = false;
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!parsed)
            {
                var bytesRead = await stream.ReadAsync(received.AsMemory(receivedCount), readCts.Token);
                Ensure(bytesRead > 0, "server should return a handshake error before closing");
                receivedCount += bytesRead;
                var sequence = new ReadOnlySequence<byte>(received.AsMemory(0, receivedCount));
                parsed = ProtocolV2FrameParser.TryReadFrame(
                    ref sequence,
                    new SharpLinkProtocolOptions(),
                    out responseHeader,
                    out responsePayload);
            }

            Ensure(responseHeader.Type == ProtocolV2FrameType.HandshakeResponse, "handshake response type");
            Ensure((responseHeader.Flags & ProtocolV2FrameFlags.Error) != 0, "handshake should fail");
            var error = ProtocolV2PayloadCodec.ReadError(
                responsePayload,
                responseHeader.Flags,
                SharpLinkProtocolOptions.DefaultMaxErrorMessageBytes);
            Ensure(error.Code == SharpLinkErrorCode.Unimplemented, "required capability error code");
            Ensure(await stream.ReadAsync(received, readCts.Token) == 0, "server should close rejected session");
        }
        finally
        {
            await serverCts.CancelAsync();
            if (server is IAsyncDisposable asyncServer)
                await asyncServer.DisposeAsync();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Test]
    public async Task TcpOversizedFrameShouldFailPendingUnaryAndStreamWithSameProtocolViolation()
    {
        const int maxFramePayloadBytes = SharpLinkProtocolOptions.MinMaxFramePayloadBytes;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var fakeServerTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync();
            await using var stream = new NetworkStream(socket, ownsSocket: true);

            var handshake = new PooledByteBufferWriter();
            var handshakeToken = ProtocolV2FrameWriter.BeginFrame(
                handshake,
                ProtocolV2FrameType.HandshakeResponse,
                ProtocolV2FrameFlags.None,
                0);
            ProtocolV2PayloadCodec.WriteHandshakeResponse(handshake, new ProtocolV2HandshakeResponse(
                ProtocolV2Constants.MinorVersion,
                ProtocolV2Capabilities.ContractManifest,
                maxFramePayloadBytes,
                1024 * 1024,
                16 * 1024 * 1024));
            ContractManifestTestHelper.EndHandshakeAndWriteManifest(handshake, handshakeToken, typeof(ITestService));
            await stream.WriteAsync(handshake.WrittenMemory);
            await stream.FlushAsync();

            await Task.Delay(150);

            var maliciousHeader = new byte[ProtocolV2Constants.HeaderBytes];
            maliciousHeader[0] = ProtocolV2Constants.Magic;
            BinaryPrimitives.WriteInt32LittleEndian(
                maliciousHeader.AsSpan(1, sizeof(int)),
                maxFramePayloadBytes + 1);
            maliciousHeader[5] = (byte)ProtocolV2FrameType.Response;
            BinaryPrimitives.WriteUInt64LittleEndian(maliciousHeader.AsSpan(7, sizeof(ulong)), 1);
            await stream.WriteAsync(maliciousHeader);
            await stream.FlushAsync();
        });

        var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), port)

            .UseProtocol(static options => options.MaxFramePayloadBytes = maxFramePayloadBytes)
            .UseHeartbeat(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30))
            .Build();

        try
        {
            await client.ConnectAsync();
            Ensure(client.State == SharpLinkConnectionState.Ready, "fake server handshake");
            var svc = client.Get<ITestService>();

            var unaryTask = svc.SlowAddAsync(1, 2, CancellationToken.None).AsTask();
            var streamTask = CollectAsync(
                svc.SlowDownloadAsync(100, 200, CancellationToken.None),
                CancellationToken.None);

            var unaryError = await CaptureSharpLinkException(unaryTask, "oversized frame unary");
            var streamError = await CaptureSharpLinkException(streamTask, "oversized frame stream");

            Ensure(unaryError.Code == SharpLinkErrorCode.ProtocolViolation, "unary protocol violation");
            Ensure(streamError.Code == SharpLinkErrorCode.ProtocolViolation, "stream protocol violation");
            Ensure(ReferenceEquals(unaryError, streamError), "pending operations should receive the first failure instance");
        }
        finally
        {
            await client.DisposeAsync();
            listener.Stop();
            await Task.WhenAny(fakeServerTask, Task.Delay(1000, CancellationToken.None));
        }
    }
}
