namespace SharpLink.IntegrationTests;

public partial class TransportConnectionIntegrationTests
{
    [Test]
    public async Task TcpServerShouldProcessHealthCheckCoalescedWithHandshake()
    {
        using var serverCts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback);
        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        var serverTask = server.RunUntilStoppedAsync(serverCts.Token).AsTask();

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
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

            // Coalesce legal control traffic without assuming TCP read boundaries or an RPC binding.
            await stream.WriteAsync(frames.WrittenMemory, timeout.Token);

            var received = new byte[4096];
            var receivedCount = 0;
            var consumedCount = 0;
            var responseCount = 0;
            while (responseCount < 2)
            {
                var bytesRead = await stream.ReadAsync(received.AsMemory(receivedCount), timeout.Token);
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
                    {
                        Ensure(header.Type == ProtocolV2FrameType.HandshakeResponse &&
                               header.RequestId == 0 && header.Flags == ProtocolV2FrameFlags.None,
                            "the successful handshake response must precede the health response");
                    }
                    else
                    {
                        Ensure(responseCount == 1 && header.Type == ProtocolV2FrameType.HealthResponse &&
                               header.RequestId == 1 && header.Flags == ProtocolV2FrameFlags.None,
                            "the coalesced health response must match the single request");
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
    [NotInParallel]
    [Arguments((ushort)3)]
    [Arguments((ushort)4)]
    public async Task TcpShouldRejectPublishedOlderMinorAndDispatchOnlyAfterPrepare(ushort legacyMinor)
    {
        const string authToken = "prepared-test-client";
        var authCalls = 0;
        PreparedTcpDispatchProbe.Reset();
        using var serverCts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback)
            .UseAuthenticator(CreateServerAuthenticator(message =>
            {
                Interlocked.Increment(ref authCalls);
                return message == authToken
                    ? SharpLinkAuthenticationResult.Success
                    : SharpLinkAuthenticationResult.Reject();
            }))
            .RequireAuthentication();
        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        var serverTask = server.RunUntilStoppedAsync(serverCts.Token).AsTask();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await VerifyRejectedHandshakeAsync(port, legacyMinor, authToken, timeout.Token);
            Ensure(Volatile.Read(ref authCalls) == 0 && PreparedTcpDispatchProbe.InvocationCount == 0,
                "an incompatible offer must close before authentication or dispatch");

            await using var client = SharpClientBuilder.Create()
                .UseRequestTimeout(TimeSpan.FromSeconds(2))
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseAuthenticator(CreateClientAuthenticator(authToken))
                .Build();
            await client.ConnectAsync(timeout.Token);
            Ensure(client.State == SharpLinkConnectionState.Ready && Volatile.Read(ref authCalls) == 1,
                "a generated current-version client must connect with the same authentication token");

            var proxy = client.CreateProxy<IPreparedTcpDispatchProbe>();
            async Task TouchBeforePrepareAsync() => _ = await proxy.Service.TouchAsync();
            await EnsureThrowsSharpLink(
                TouchBeforePrepareAsync(), "unprepared TCP proxy", SharpLinkErrorCode.Unavailable);
            Ensure(PreparedTcpDispatchProbe.InvocationCount == 0,
                "a connected proxy must not dispatch before preparation");

            await proxy.PrepareAsync(TimeSpan.FromSeconds(2), timeout.Token);
            var result = await proxy.Service.TouchAsync().AsTask().WaitAsync(timeout.Token);
            Ensure(result == 1 && PreparedTcpDispatchProbe.InvocationCount == 1 &&
                   Volatile.Read(ref authCalls) == 1,
                "the same prepared proxy must dispatch exactly once without reconnecting");
        }
        finally
        {
            await serverCts.CancelAsync();
            await server.DisposeAsync();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static async Task VerifyRejectedHandshakeAsync(
        int port, ushort minor, string authToken, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, port, ct);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        using var frame = new PooledByteBufferWriter();
        var limits = new SharpLinkProtocolOptions();
        var token = ProtocolV2FrameWriter.BeginFrame(
            frame, ProtocolV2FrameType.HandshakeRequest, ProtocolV2FrameFlags.None, 0);
        ProtocolV2PayloadCodec.WriteHandshakeRequest(
            frame,
            new ProtocolV2HandshakeRequest(
                minor, ProtocolV2Capabilities.None, ProtocolV2Capabilities.None,
                SharpLinkProtocolOptions.DefaultMaxFramePayloadBytes,
                1024 * 1024, 16 * 1024 * 1024, System.Text.Encoding.UTF8.GetBytes(authToken)),
            limits);
        ProtocolV2FrameWriter.EndFrame(frame, token);
        await stream.WriteAsync(frame.WrittenMemory, ct);

        var headerBytes = new byte[ProtocolV2Constants.HeaderBytes];
        await stream.ReadExactlyAsync(headerBytes, ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.AsSpan(1));
        Ensure(length >= 0 && length <= limits.MaxFramePayloadBytes, "handshake response length");
        var bytes = new byte[headerBytes.Length + length];
        headerBytes.CopyTo(bytes, 0);
        await stream.ReadExactlyAsync(bytes.AsMemory(headerBytes.Length), ct);
        var sequence = new ReadOnlySequence<byte>(bytes);
        Ensure(ProtocolV2FrameParser.TryReadFrame(ref sequence, limits, out var header, out var payload) &&
               sequence.IsEmpty && header.Type == ProtocolV2FrameType.HandshakeResponse &&
               header.RequestId == 0 && (header.Flags & ProtocolV2FrameFlags.Error) != 0,
            "the previous minor must receive a handshake error");
        var error = ProtocolV2PayloadCodec.ReadError(payload, header.Flags, limits.MaxErrorMessageBytes);
        Ensure(error.Code == SharpLinkErrorCode.Unimplemented, "incompatible wire minor error code");
        Ensure(await stream.ReadAsync(headerBytes.AsMemory(0, 1), ct) == 0, "rejected connection must close");
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
        var serverTask = server.RunUntilStoppedAsync(serverCts.Token).AsTask();

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
        var serverTask = server.RunUntilStoppedAsync(serverCts.Token).AsTask();

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
            .UseTcp(0, IPAddress.Loopback)

            .UseProtocol(static options => options.HandshakeTimeout = TimeSpan.FromMilliseconds(120));
        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        var serverTask = server.RunUntilStoppedAsync(serverCts.Token).AsTask();

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
            .UseTcp(0, IPAddress.Loopback)
            ;
        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        var serverTask = server.RunUntilStoppedAsync(serverCts.Token).AsTask();

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
    public async Task TcpTerminalProtocolFailureShouldReachPendingUnaryAndOutputStream()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = budget.Token;
        var unaryStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStream = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverBuilder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback);
        serverBuilder.ReplaceService<IConnectionBehaviorService>(new ConnectionBehaviorService
        {
            SlowUnaryStarted = unaryStarted,
            ContinueSlowRange = releaseStream.Task,
        });
        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        await using var server = serverBuilder.Build();
        await server.StartAsync(ct);
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .Build();
        await client.ConnectAsync(ct);
        var service = await client.PrepareTestServiceAsync<IConnectionBehaviorService>();
        var ready = (ClientConnection[])typeof(SharpLinkClient).GetField("_readyConnections",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(client)!;
        var session = ready.Single().Session;

        await using var output = service.SlowRangeAsync(count: 2, delayMs: 0, CancellationToken.None)
            .GetAsyncEnumerator();
        try
        {
            var unary = service.SlowAsync(delayMs: 10_000, ct).AsTask();
            await unaryStarted.Task.WaitAsync(ct);
            Ensure(await output.MoveNextAsync().AsTask().WaitAsync(ct) && output.Current == 0,
                "the prepared output stream must already be active");
            var next = output.MoveNextAsync().AsTask();
            Ensure(!unary.IsCompleted && !next.IsCompleted, "both operations must still be in flight");

            // Parser size checks are covered independently: exercise the same
            // terminal error fan-out on a real prepared connection, without a fake peer.
            var cause = new SharpLinkProtocolViolationException(ProtocolViolationReason.MalformedFrame,
                "The session reported a protocol failure.");
            session.NotifyDisconnected(cause);

            var unaryError = await CaptureSharpLinkException(unary, "protocol failure on unary");
            var streamError = await CaptureSharpLinkException(next, "protocol failure on output stream");
            Ensure(ReferenceEquals(cause, unaryError) && ReferenceEquals(cause, streamError),
                "both pending operations must see the exact terminal failure");
        }
        finally
        {
            releaseStream.TrySetResult();
        }
    }

}

[RpcContract]
public interface IPreparedTcpDispatchProbe : IService
{
    [NonCancellable]
    ValueTask<int> TouchAsync();
}

[RpcService]
public sealed class PreparedTcpDispatchProbe : IPreparedTcpDispatchProbe
{
    private static int s_invocationCount;
    internal static int InvocationCount => Volatile.Read(ref s_invocationCount);
    internal static void Reset() => Interlocked.Exchange(ref s_invocationCount, 0);
    public ValueTask<int> TouchAsync() => ValueTask.FromResult(Interlocked.Increment(ref s_invocationCount));
}
