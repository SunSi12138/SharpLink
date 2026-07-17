namespace SharpLink.IntegrationTests;

public class TransportConnectionIntegrationTests
{
    [Test]
    public async Task TcpConnectAndBasicRpcShouldWork()
    {
        await using var harness = await TransportHarness.CreateAsync(TransportKind.Tcp);
        var svc = harness.Client.Get<IConnectionBehaviorService>();

        var value = await svc.PingAsync(7);
        Ensure(value == 8, "tcp ping");
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
    public async Task NamedPipeConnectAndBasicRpcShouldWork()
    {
        await using var harness = await TransportHarness.CreateAsync(TransportKind.NamedPipe);
        var svc = harness.Client.Get<IConnectionBehaviorService>();

        var value = await svc.PingAsync(9);
        Ensure(value == 10, "namedpipe ping");
    }

    [Test]
    public async Task UdsConnectAndBasicRpcShouldWork()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
            return;

        await using var harness = await TransportHarness.CreateAsync(TransportKind.Uds);
        var svc = harness.Client.Get<IConnectionBehaviorService>();

        var value = await svc.PingAsync(11);
        Ensure(value == 12, "uds ping");
    }

    [Test]
    public async Task TcpServerUnexpectedDisconnectShouldFailFastPendingCall()
    {
        await using var harness = await TransportHarness.CreateAsync(TransportKind.Tcp);
        var svc = harness.Client.Get<IConnectionBehaviorService>();

        var pending = svc.SlowAsync(2000, CancellationToken.None).AsTask();
        await Task.Delay(120);
        await harness.DisposeServerOnlyAsync();

        await EnsureThrowsSharpLinkFast(pending, "tcp pending should fail fast after server dispose", SharpLinkErrorCode.ConnectionClosed);
    }

    [Test]
    public async Task NamedPipeServerUnexpectedDisconnectShouldFailFastPendingCall()
    {
        await using var harness = await TransportHarness.CreateAsync(TransportKind.NamedPipe);
        var svc = harness.Client.Get<IConnectionBehaviorService>();

        var pending = svc.SlowAsync(2000, CancellationToken.None).AsTask();
        await Task.Delay(120);
        await harness.DisposeServerOnlyAsync();

        await EnsureThrowsSharpLinkFast(pending, "namedpipe pending should fail fast after server dispose", SharpLinkErrorCode.ConnectionClosed);
    }

    [Test]
    public async Task UdsServerUnexpectedDisconnectShouldFailFastPendingCall()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
            return;

        await using var harness = await TransportHarness.CreateAsync(TransportKind.Uds);
        var svc = harness.Client.Get<IConnectionBehaviorService>();

        var pending = svc.SlowAsync(2000, CancellationToken.None).AsTask();
        await Task.Delay(120);
        await harness.DisposeServerOnlyAsync();

        await EnsureThrowsSharpLinkFast(pending, "uds pending should fail fast after server dispose", SharpLinkErrorCode.ConnectionClosed);
    }

    [Test]
    public async Task TcpClientDisposeShouldFailFastPendingCall()
    {
        await using var harness = await TransportHarness.CreateAsync(TransportKind.Tcp);
        var svc = harness.Client.Get<IConnectionBehaviorService>();

        var pending = svc.SlowAsync(2000, CancellationToken.None).AsTask();
        await Task.Delay(120);
        await harness.DisposeClientOnlyAsync();

        await EnsureThrowsSharpLinkFast(pending, "tcp pending should fail fast after client dispose", SharpLinkErrorCode.ConnectionClosed);
    }

    [Test]
    public async Task NamedPipeClientDisposeShouldFailFastPendingCall()
    {
        await using var harness = await TransportHarness.CreateAsync(TransportKind.NamedPipe);
        var svc = harness.Client.Get<IConnectionBehaviorService>();

        var pending = svc.SlowAsync(2000, CancellationToken.None).AsTask();
        await Task.Delay(120);
        await harness.DisposeClientOnlyAsync();

        await EnsureThrowsSharpLinkFast(pending, "namedpipe pending should fail fast after client dispose", SharpLinkErrorCode.ConnectionClosed);
    }

    [Test]
    public async Task UdsClientDisposeShouldFailFastPendingCall()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
            return;

        await using var harness = await TransportHarness.CreateAsync(TransportKind.Uds);
        var svc = harness.Client.Get<IConnectionBehaviorService>();

        var pending = svc.SlowAsync(2000, CancellationToken.None).AsTask();
        await Task.Delay(120);
        await harness.DisposeClientOnlyAsync();

        await EnsureThrowsSharpLinkFast(pending, "uds pending should fail fast after client dispose", SharpLinkErrorCode.ConnectionClosed);
    }

    [Test]
    public async Task TcpConnectWithoutServerShouldThrowSocketException()
    {
        var port = GetFreePort();
        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            await EnsureThrows<SocketException>(client.ConnectAsync(), "tcp connect without server");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task NamedPipeConnectWithoutServerShouldHonorCancellation()
    {
        var pipeName = $"sharplink-int-no-server-{Guid.NewGuid():N}";
        var client = SharpClientBuilder.Create()
            .UseNamedPipe(pipeName)
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(180));
            await EnsureThrows<OperationCanceledException>(
                client.ConnectAsync(cts.Token),
                "namedpipe connect without server");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task UdsConnectWithoutServerShouldThrowSocketException()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
            return;

        var socketPath = GetUniqueUdsPath();
        var client = SharpClientBuilder.Create()
            .UseUds(socketPath)
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            await EnsureThrows<SocketException>(client.ConnectAsync(), "uds connect without server");
        }
        finally
        {
            await client.DisposeAsync();
            TryDeleteFile(socketPath);
        }
    }

    [Test]
    public async Task TcpConnectWithCanceledTokenShouldThrowOperationCanceledException()
    {
        var port = GetFreePort();
        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            await EnsureThrows<OperationCanceledException>(
                client.ConnectAsync(cts.Token),
                "tcp connect with canceled token");
        }
        finally
        {
            await client.DisposeAsync();
        }
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
        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseSerializer(MemoryPackCodec.Resolver)
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
        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseSerializer(MemoryPackCodec.Resolver)
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
            .AddService<IConnectionBehaviorService, ConnectionBehaviorService>()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .UseSerializer(MemoryPackCodec.Resolver)
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

        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseSerializer(MemoryPackCodec.Resolver)
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
            .AddService<IConnectionBehaviorService, ConnectionBehaviorService>()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .UseSerializer(MemoryPackCodec.Resolver);
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
                    ProtocolV2Capabilities.Compression,
                    ProtocolV2Capabilities.Compression,
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
                ProtocolV2Capabilities.None,
                maxFramePayloadBytes,
                1024 * 1024,
                16 * 1024 * 1024));
            ProtocolV2FrameWriter.EndFrame(handshake, handshakeToken);
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

        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseSerializer(MemoryPackCodec.Resolver)
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

    [Test]
    public async Task TcpCustomAuthenticatorShouldAcceptMatchingHandshakeMessage()
    {
        using var cts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .AddService<IConnectionBehaviorService, ConnectionBehaviorService>()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseAuthenticator(CreateServerAuthenticator(static message => message == "expected-token"
                ? SharpLinkAuthenticationResult.Success
                : SharpLinkAuthenticationResult.Reject()))
            .RequireAuthentication()
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();

        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
            {
                IgnoreExpectedException(ex);
            }
        }, CancellationToken.None);

        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseAuthenticator(CreateClientAuthenticator("expected-token"))
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            await client.ConnectAsync(cts.Token);
            Ensure(client.State == SharpLinkConnectionState.Ready, "custom authenticator should connect");
            var svc = client.Get<IConnectionBehaviorService>();
            Ensure(await svc.PingAsync(12) == 13, "custom authenticator ping");
        }
        finally
        {
            await client.DisposeAsync();
            await cts.CancelAsync();
            await server.DisposeAsync();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    [Test]
    public async Task TcpCustomAuthenticatorShouldRejectMismatchedHandshakeMessage()
    {
        using var cts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .AddService<IConnectionBehaviorService, ConnectionBehaviorService>()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseAuthenticator(CreateServerAuthenticator(static message => message == "expected-token"
                ? SharpLinkAuthenticationResult.Success
                : SharpLinkAuthenticationResult.Reject()))
            .RequireAuthentication()
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();

        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
            {
                IgnoreExpectedException(ex);
            }
        }, CancellationToken.None);

        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseAuthenticator(CreateClientAuthenticator("unexpected-token"))
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            var exception = await CaptureSharpLinkException(
                client.ConnectAsync(cts.Token).AsTask(),
                "custom authenticator rejection");
            Ensure(exception.Code == SharpLinkErrorCode.AuthenticationRejected, "custom authenticator diagnostics should expose authentication rejection");
        }
        finally
        {
            await client.DisposeAsync();
            await cts.CancelAsync();
            await server.DisposeAsync();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    [Test]
    public async Task TcpStructuredAuthenticatorShouldExposeCustomAuthenticationError()
    {
        using var cts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .AddService<IConnectionBehaviorService, ConnectionBehaviorService>()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseAuthenticator(CreateServerAuthenticator(static message => message == "expected-token"
                ? SharpLinkAuthenticationResult.Success
                : SharpLinkAuthenticationResult.Reject(
                    SharpLinkErrorCode.AuthenticationExpired,
                    "token expired")))
            .RequireAuthentication()
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();

        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
            {
                IgnoreExpectedException(ex);
            }
        }, CancellationToken.None);

        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseAuthenticator(CreateClientAuthenticator("expired-token"))
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            var exception = await CaptureSharpLinkException(
                client.ConnectAsync(cts.Token).AsTask(),
                "structured authenticator rejection");
            Ensure(exception.Code == SharpLinkErrorCode.AuthenticationExpired, "structured authenticator code");
            Ensure(exception.Message == "token expired", "structured authenticator message");
        }
        finally
        {
            await client.DisposeAsync();
            await cts.CancelAsync();
            await server.DisposeAsync();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    [Test]
    public async Task TcpAuthenticatorShouldRejectExpiredContextDuringHandshake()
    {
        using var cts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .AddService<IConnectionBehaviorService, ConnectionBehaviorService>()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseAuthenticator(SharpLinkAuthenticator.CreateServer(static (_, _) => ValueTask.FromResult(
                SharpLinkAuthenticationResult.Authenticate(
                    new SharpLinkAuthenticationContext(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1))))))
            .RequireAuthentication()
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        var serverTask = Task.Run(() => server.RunAsync(cts.Token).AsTask(), CancellationToken.None);
        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseSerializer(MemoryPackCodec.Resolver)
            .Build();

        try
        {
            var exception = await CaptureSharpLinkException(
                client.ConnectAsync(cts.Token).AsTask(),
                "expired authentication context");
            Ensure(exception.Code == SharpLinkErrorCode.AuthenticationExpired, "expired context code");
        }
        finally
        {
            await client.DisposeAsync();
            await cts.CancelAsync();
            await server.DisposeAsync();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    [Test]
    public async Task TcpClientShouldRejectOversizedAuthenticationPayloadBeforeSend()
    {
        const int maxAuthenticationBytes = 32;
        using var cts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .AddService<IConnectionBehaviorService, ConnectionBehaviorService>()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseProtocol(static options => options.MaxMetadataBytes = maxAuthenticationBytes);

        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        var serverTask = Task.Run(() => server.RunAsync(cts.Token).AsTask(), CancellationToken.None);
        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseProtocol(static options => options.MaxMetadataBytes = maxAuthenticationBytes)
            .UseAuthenticator(SharpLinkAuthenticator.CreateClient(static _ =>
                ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[maxAuthenticationBytes + 1])))
            .Build();

        try
        {
            var exception = await CaptureSharpLinkException(
                client.ConnectAsync(cts.Token).AsTask(),
                "oversized authentication payload");
            Ensure(exception.Code == SharpLinkErrorCode.ResourceExhausted, "authentication payload limit code");
        }
        finally
        {
            await client.DisposeAsync();
            await cts.CancelAsync();
            await server.DisposeAsync();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    [Test]
    public async Task TcpStructuredAuthenticatorShouldExposeAuthenticationContextToService()
    {
        var expiresAt = new DateTimeOffset(2030, 4, 19, 12, 34, 56, TimeSpan.Zero);
        using var cts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .AddService<IConnectionBehaviorService, ConnectionBehaviorService>()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseAuthenticator(CreateServerAuthenticator(static message => message == "expected-token"
                ? SharpLinkAuthenticationResult.Authenticate(
                    new SharpLinkAuthenticationContext(
                        subject: "user-42",
                        tenantId: "tenant-a",
                        scopes: ["rpc.read", "rpc.write"],
                        expiresAt: new DateTimeOffset(2030, 4, 19, 12, 34, 56, TimeSpan.Zero),
                        claims: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["role"] = "admin"
                        }))
                : SharpLinkAuthenticationResult.Reject()))
            .RequireAuthentication()
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();

        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
            {
                IgnoreExpectedException(ex);
            }
        }, CancellationToken.None);

        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseAuthenticator(CreateClientAuthenticator("expected-token"))
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            await client.ConnectAsync(cts.Token);
            Ensure(client.State == SharpLinkConnectionState.Ready, "structured authenticator should connect");
            var svc = client.Get<IConnectionBehaviorService>();
            Ensure(await svc.GetAuthenticationSummaryAsync() == "user-42|admin", "authentication context should flow into service");
            Ensure(
                await svc.GetAuthenticationDetailsAsync() == $"tenant-a|True|True|{expiresAt:O}",
                "tenant/scopes/expiresAt should flow into service");
        }
        finally
        {
            await client.DisposeAsync();
            await cts.CancelAsync();
            await server.DisposeAsync();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    [Test]
    public async Task TcpAuthorizationGuardsShouldReturnStructuredRemoteErrors()
    {
        var expiresAt = new DateTimeOffset(2030, 4, 19, 12, 34, 56, TimeSpan.Zero);
        using var cts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .AddService<IConnectionBehaviorService, ConnectionBehaviorService>()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseAuthenticator(CreateServerAuthenticator(static message => message == "expected-token"
                ? SharpLinkAuthenticationResult.Authenticate(
                    new SharpLinkAuthenticationContext(
                        subject: "user-42",
                        tenantId: "tenant-a",
                        scopes: ["rpc.read"],
                        expiresAt: new DateTimeOffset(2030, 4, 19, 12, 34, 56, TimeSpan.Zero),
                        claims: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["role"] = "admin"
                        }))
                : SharpLinkAuthenticationResult.Reject()))
            .RequireAuthentication()
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();

        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
            {
                IgnoreExpectedException(ex);
            }
        }, CancellationToken.None);

        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseAuthenticator(CreateClientAuthenticator("expected-token"))
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            await client.ConnectAsync(cts.Token);
            Ensure(client.State == SharpLinkConnectionState.Ready, "authorization guard client should connect");
            var svc = client.Get<IConnectionBehaviorService>();

            await EnsureThrowsSharpLink(
                svc.RequireScopeAsync("rpc.write").AsTask(),
                "scope guard",
                SharpLinkErrorCode.AuthorizationDenied,
                "rpc.write");

            await EnsureThrowsSharpLink(
                svc.RequireTenantAsync("tenant-b").AsTask(),
                "tenant guard",
                SharpLinkErrorCode.AuthorizationDenied,
                "tenant-b");

            await EnsureThrowsSharpLink(
                svc.RequireActiveTokenAsync(expiresAt.ToUnixTimeSeconds() + 1).AsTask(),
                "expiry guard",
                SharpLinkErrorCode.AuthenticationExpired,
                "expired");
        }
        finally
        {
            await client.DisposeAsync();
            await cts.CancelAsync();
            await server.DisposeAsync();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    [Test]
    public async Task TcpServerStartShouldStopNormallyWhenCancellationIsRequested()
    {
        var server = SharpLinkServerBuilder.Create()
            .AddService<IConnectionBehaviorService, ConnectionBehaviorService>()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await server.RunAsync(cts.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Test]
    public async Task NamedPipeServerStartShouldStopNormallyWhenCancellationIsRequested()
    {
        var pipeName = $"sharplink-start-cancel-{Guid.NewGuid():N}";
        var server = SharpLinkServerBuilder.Create()
            .AddService<IConnectionBehaviorService, ConnectionBehaviorService>()
            .UseNamedPipe(pipeName)
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await server.RunAsync(cts.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Test]
    public async Task ServerStartShouldSurfaceTransportAcceptException()
    {
        var server = SharpLinkServerBuilder.Create()
            .AddService<IConnectionBehaviorService, ConnectionBehaviorService>()
            .UseTransport(new ThrowingConnectTransport(new IOException("accept failed")))
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            await EnsureThrows<IOException>(server.RunAsync(CancellationToken.None).AsTask(), "server start transport accept exception");
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Test]
    public async Task TcpShouldReconnectWithNewClientInstanceAfterDisconnect()
    {
        await VerifyReconnectWithNewClientInstanceAsync(TransportKind.Tcp);
    }

    [Test]
    public async Task NamedPipeShouldReconnectWithNewClientInstanceAfterDisconnect()
    {
        await VerifyReconnectWithNewClientInstanceAsync(TransportKind.NamedPipe);
    }

    [Test]
    public async Task UdsShouldReconnectWithNewClientInstanceAfterDisconnect()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
            return;

        await VerifyReconnectWithNewClientInstanceAsync(TransportKind.Uds);
    }


    private static ISharpLinkClientAuthenticator CreateClientAuthenticator(string token)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(token);
        return SharpLinkAuthenticator.CreateClient(
            _ => ValueTask.FromResult<ReadOnlyMemory<byte>>(payload));
    }

    private static ISharpLinkServerAuthenticator CreateServerAuthenticator(
        Func<string, SharpLinkAuthenticationResult> authenticate)
    {
        return SharpLinkAuthenticator.CreateServer((request, _) => ValueTask.FromResult(
            authenticate(System.Text.Encoding.UTF8.GetString(request.Payload.Span))));
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

    private static Task EnsureThrows<TException>(ValueTask task, string name) where TException : Exception
        => EnsureThrows<TException>(task.AsTask(), name);

    private static Task EnsureThrowsSharpLink(
        ValueTask task,
        string name,
        SharpLinkErrorCode errorCode,
        string? messageContains = null)
        => EnsureThrowsSharpLink(task.AsTask(), name, errorCode, messageContains);

    private static async Task EnsureThrowsSharpLinkFast(Task task, string name, SharpLinkErrorCode errorCode)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            throw new Exception($"assert failed: {name} should throw");
        }
        catch (TimeoutException)
        {
            throw new Exception($"assert failed: {name} did not fail fast");
        }
        catch (SharpLinkException ex)
        {
            Ensure(ex.Code == errorCode, $"{name} error code");
        }
    }

    private static async Task<SharpLinkException> CaptureSharpLinkException(Task task, string name)
    {
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
            return ex;
        }
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> stream, CancellationToken ct)
    {
        var list = new List<T>();
        await foreach (var item in stream.WithCancellation(ct))
            list.Add(item);
        return list;
    }

    private static async Task EnsureThrowsSharpLink(Task task, string name, SharpLinkErrorCode errorCode, string? messageContains = null)
    {
        try
        {
            await task;
            throw new Exception($"assert failed: {name} should throw SharpLinkException");
        }
        catch (SharpLinkException ex)
        {
            Ensure(ex.Code == errorCode, $"{name} error code");
            if (!string.IsNullOrWhiteSpace(messageContains))
                Ensure(ex.Message.Contains(messageContains, StringComparison.Ordinal), $"{name} error message");
        }
    }

    private static void Ensure(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"assert failed: {name}");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string GetUniqueUdsPath()
    {
        return Path.Combine(Path.GetTempPath(), $"sharplink-{Guid.NewGuid():N}.sock");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or NotSupportedException)
        {
            IgnoreExpectedException(ex);
        }
    }

    private static async Task VerifyReconnectWithNewClientInstanceAsync(TransportKind kind)
    {
        await using var harness1 = await TransportHarness.CreateAsync(kind);
        var svc1 = harness1.Client.Get<IConnectionBehaviorService>();
        Ensure(await svc1.PingAsync(20) == 21, $"{kind} first connect");
        await harness1.DisposeClientOnlyAsync();
        await Task.Delay(120);

        var client2 = BuildClientForEndpoint(harness1.Endpoint);
        try
        {
            await client2.ConnectAsync();
            Ensure(client2.State == SharpLinkConnectionState.Ready, $"{kind} reconnect connect");
            var svc2 = client2.Get<IConnectionBehaviorService>();
            Ensure(await svc2.PingAsync(30) == 31, $"{kind} reconnect ping");
        }
        finally
        {
            await client2.DisposeAsync();
        }
    }

    private static async Task VerifyNegotiatedFrameLimitAsync(int clientLimit, int serverLimit)
    {
        using var cts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .AddService<IConnectionBehaviorService, ConnectionBehaviorService>()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseProtocol(options => options.MaxFramePayloadBytes = serverLimit)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        var serverTask = Task.Run(() => server.RunAsync(cts.Token).AsTask(), CancellationToken.None);
        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseProtocol(options => options.MaxFramePayloadBytes = clientLimit)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2))
            .Build();
        try
        {
            await client.ConnectAsync(cts.Token);
            var service = client.Get<IConnectionBehaviorService>();
            await EnsureThrowsSharpLink(
                service.EchoAsync(new string('x', 2_048)).AsTask(),
                "oversized request",
                SharpLinkErrorCode.ResourceExhausted);
            Ensure(await service.PingAsync(1) == 2, "connection remains healthy after request rejection");

            await EnsureThrowsSharpLink(
                service.CreatePayloadAsync(2_048).AsTask(),
                "oversized response",
                SharpLinkErrorCode.ResourceExhausted);
            Ensure(await service.PingAsync(2) == 3, "connection remains healthy after response rejection");
        }
        finally
        {
            await client.DisposeAsync();
            await cts.CancelAsync();
            await server.StopAsync(TimeSpan.Zero);
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    private static ISharpLinkClient BuildClientForEndpoint(TransportEndpoint endpoint)
    {
        var builder = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

        switch (endpoint.Kind)
        {
            case TransportKind.Tcp:
                builder.UseTcp(IPAddress.Loopback.ToString(), endpoint.Port);
                break;
            case TransportKind.NamedPipe:
                builder.UseNamedPipe(endpoint.PipeName);
                break;
            case TransportKind.Uds:
                builder.UseUds(endpoint.UdsPath);
                break;
            default:
                throw new InvalidOperationException($"Unsupported endpoint kind: {endpoint.Kind}");
        }

        return builder.Build();
    }

    private enum TransportKind
    {
        Tcp,
        NamedPipe,
        Uds
    }

    private readonly record struct TransportEndpoint(TransportKind Kind, int Port, string PipeName, string UdsPath);

    private sealed class ThrowingConnectTransport(Exception exception) : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => null;

        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(exception);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TransportHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;
        private readonly Action _cleanup;
        private bool _serverDisposed;
        private bool _clientDisposed;

        public ISharpLinkClient Client { get; }
        public TransportEndpoint Endpoint { get; }

        private TransportHarness(
            ISharpLinkServer server,
            Task serverTask,
            CancellationTokenSource serverCts,
            ISharpLinkClient client,
            TransportEndpoint endpoint,
            Action cleanup)
        {
            _server = server;
            _serverTask = serverTask;
            _serverCts = serverCts;
            Client = client;
            Endpoint = endpoint;
            _cleanup = cleanup;
        }

        public static Task<TransportHarness> CreateAsync(TransportKind kind) => CreateAsync(kind, default);

        private static async Task<TransportHarness> CreateAsync(TransportKind kind, TransportEndpoint endpoint)
        {
            var cts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .AddService<IConnectionBehaviorService, ConnectionBehaviorService>()
                .UseSerializer(MemoryPackCodec.Resolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

            var clientBuilder = SharpClientBuilder.Create()
                .UseSerializer(MemoryPackCodec.Resolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

            Action cleanup = static () => { };
            TransportEndpoint resolvedEndpoint = endpoint;

            switch (kind)
            {
                case TransportKind.Tcp:
                {
                    serverBuilder.UseTcp(endpoint.Port, IPAddress.Loopback.ToString());
                    var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
                    clientBuilder.UseTcp(IPAddress.Loopback.ToString(), port);
                    resolvedEndpoint = new TransportEndpoint(kind, port, string.Empty, string.Empty);
                    break;
                }
                case TransportKind.NamedPipe:
                {
                    var pipeName = string.IsNullOrWhiteSpace(endpoint.PipeName)
                        ? $"sharplink-int-{Guid.NewGuid():N}"
                        : endpoint.PipeName;
                    serverBuilder.UseNamedPipe(pipeName);
                    clientBuilder.UseNamedPipe(pipeName);
                    resolvedEndpoint = new TransportEndpoint(kind, 0, pipeName, string.Empty);
                    break;
                }
                case TransportKind.Uds:
                {
                    if (!Socket.OSSupportsUnixDomainSockets)
                        throw new PlatformNotSupportedException("Unix domain sockets are not supported on this platform.");

                    var udsPath = string.IsNullOrWhiteSpace(endpoint.UdsPath)
                        ? GetUniqueUdsPath()
                        : endpoint.UdsPath;
                    serverBuilder.UseUds(udsPath);
                    clientBuilder.UseUds(udsPath);
                    resolvedEndpoint = new TransportEndpoint(kind, 0, string.Empty, udsPath);
                    cleanup = () => TryDeleteFile(udsPath);
                    break;
                }
            }

            var server = serverBuilder.Build();
            var serverTask = Task.Run(async () =>
            {
                try
                {
                    await server.RunAsync(cts.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
                {
                    IgnoreExpectedException(ex);
                }
            }, CancellationToken.None);

            var client = clientBuilder.Build();
            await client.ConnectAsync(cts.Token);

            return new TransportHarness(server, serverTask, cts, client, resolvedEndpoint, cleanup);
        }

        public async ValueTask DisposeServerOnlyAsync()
        {
            if (_serverDisposed)
                return;

            _serverDisposed = true;
            try
            {
                await _server.StopAsync(TimeSpan.Zero);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or ArgumentException)
            {
                IgnoreExpectedException(ex);
            }
        }

        public async ValueTask DisposeClientOnlyAsync()
        {
            if (_clientDisposed)
                return;

            _clientDisposed = true;
            try
            {
                await Client.StopAsync();
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or ArgumentException)
            {
                IgnoreExpectedException(ex);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeClientOnlyAsync();
            try
            {
                await _serverCts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // already disposed by racing cleanup path
            }
            await DisposeServerOnlyAsync();
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            try
            {
                _serverCts.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // already disposed by racing cleanup path
            }
            _cleanup();
        }
    }

    private static void IgnoreExpectedException(Exception ex)
    {
        _ = ex.HResult;
    }
}

[RpcContract]
public interface IConnectionBehaviorService : IService
{
    [NonCancellable]
    ValueTask<int> PingAsync(int value);
    [NonCancellable]
    ValueTask<string> EchoAsync(string value);
    [NonCancellable]
    ValueTask<string> CreatePayloadAsync(int length);
    ValueTask<int> SlowAsync(int delayMs, CancellationToken cancellationToken = default);
    [NonCancellable]
    ValueTask<string> GetAuthenticationSummaryAsync();
    [NonCancellable]
    ValueTask<string> GetAuthenticationDetailsAsync();
    [NonCancellable]
    ValueTask<int> RequireScopeAsync(string scope);
    [NonCancellable]
    ValueTask<int> RequireTenantAsync(string tenantId);
    [NonCancellable]
    ValueTask<int> RequireActiveTokenAsync(long unixSeconds);
}

[RpcService]
public sealed class ConnectionBehaviorService : IConnectionBehaviorService
{
    public ValueTask<int> PingAsync(int value) => ValueTask.FromResult(value + 1);

    public ValueTask<string> EchoAsync(string value) => ValueTask.FromResult(value);

    public ValueTask<string> CreatePayloadAsync(int length)
        => ValueTask.FromResult(new string('x', length));

    public async ValueTask<int> SlowAsync(int delayMs, CancellationToken cancellationToken = default)
    {
        await Task.Delay(delayMs, cancellationToken);
        return delayMs;
    }

    public ValueTask<string> GetAuthenticationSummaryAsync()
    {
        var context = SharpLinkCallContext.Current?.Authentication;
        var subject = context?.Subject ?? "<null>";
        var role = context?.GetClaim("role") ?? "<null>";
        return ValueTask.FromResult($"{subject}|{role}");
    }

    public ValueTask<string> GetAuthenticationDetailsAsync()
    {
        var context = SharpLinkCallContext.Current?.Authentication;
        var tenantId = context?.TenantId ?? "<null>";
        var hasRead = context?.HasScope("rpc.read") ?? false;
        var hasWrite = context?.HasScope("rpc.write") ?? false;
        var expiresAt = context?.ExpiresAt?.ToString("O") ?? "<null>";
        return ValueTask.FromResult($"{tenantId}|{hasRead}|{hasWrite}|{expiresAt}");
    }

    public ValueTask<int> RequireScopeAsync(string scope)
    {
        SharpLinkAuthorization.RequireScope(scope);
        return ValueTask.FromResult(1);
    }

    public ValueTask<int> RequireTenantAsync(string tenantId)
    {
        SharpLinkAuthorization.RequireTenant(tenantId);
        return ValueTask.FromResult(1);
    }

    public ValueTask<int> RequireActiveTokenAsync(long unixSeconds)
    {
        SharpLinkAuthorization.RequireActiveToken(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));
        return ValueTask.FromResult(1);
    }
}
