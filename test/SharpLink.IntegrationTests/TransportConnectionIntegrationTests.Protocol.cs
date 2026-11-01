namespace SharpLink.IntegrationTests;

public partial class TransportConnectionIntegrationTests
{
    [Test]
    public async Task TcpServerShouldProcessRequestCoalescedWithHandshake()
    {
        using var serverCts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback)
            ;
        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        var serverTask = server.RunUntilStoppedAsync(serverCts.Token).AsTask();

        Exception? primary = null;
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
                frames, CurrentProtocolFixtureOffer(ProtocolV2Capabilities.HealthCheck), limits);
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

            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var handshake = await ReadProtocolFixtureFrameAsync(stream, limits, readCts.Token);
            Ensure(handshake.Header.Type == ProtocolV2FrameType.HandshakeResponse &&
                   handshake.Header.Flags == ProtocolV2FrameFlags.None && handshake.Header.RequestId == 0,
                "successful handshake response order");
            var response = ProtocolV2PayloadCodec.ReadHandshakeResponse(new(handshake.Payload), limits);
            Ensure((response.NegotiatedCapabilities & ProtocolV2Capabilities.HealthCheck) != 0,
                "health capability must be negotiated");
            var manifest = await ReadProtocolFixtureFrameAsync(stream, limits, readCts.Token);
            Ensure(manifest.Header.Type == ProtocolV2FrameType.ContractManifest && manifest.Header.RequestId == 0,
                "the mandatory bootstrap manifest precedes coalesced health handling");
            Ensure(ProtocolV2ContractManifestCodec.Read(new(manifest.Payload), limits).Generation == 1,
                "initial nonempty registry generation");
            var health = await ReadProtocolFixtureFrameAsync(stream, limits, readCts.Token);
            Ensure(health.Header.Type == ProtocolV2FrameType.HealthResponse && health.Header.RequestId == 1,
                "coalesced health response type and request ID");
            Ensure(ProtocolV2PayloadCodec.ReadHealthResponse(new(health.Payload)).Status == SharpLinkHealthStatus.Ready,
                "coalesced health response status");
        }
        catch (Exception error) { primary = error; }
        await CleanupProtocolFixtureAsync(primary,
            () => new ValueTask(serverCts.CancelAsync()),
            () => server.DisposeAsync(),
            () => new ValueTask(serverTask.WaitAsync(TimeSpan.FromSeconds(2))));
    }

    [Test]
    [NotInParallel]
    public async Task TcpServerShouldRejectLegacyProtocolMinorBeforeRpcTraffic()
    {
        LegacyHandshakeDispatchProbe.Reset();
        using var serverCts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback);
        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        var serverTask = server.RunUntilStoppedAsync(serverCts.Token).AsTask();
        Exception? failure = null;

        try
        {
            using var controlTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var request = await CreateActuallyBoundProbeRequestAsync(port, controlTimeout.Token);
            Ensure(LegacyHandshakeDispatchProbe.InvocationCount == 1,
                "the current-revision bound control must execute the probe exactly once");
            LegacyHandshakeDispatchProbe.Reset();

            // Exact historical revision-0 layout: 41 bytes, including preference fields,
            // empty compression/auth lengths, and NO Protocol3 16-byte binding-limits field.
            var legacy = new byte[41];
            BinaryPrimitives.WriteUInt16LittleEndian(legacy, 0);
            BinaryPrimitives.WriteInt32LittleEndian(legacy.AsSpan(18), SharpLinkProtocolOptions.DefaultMaxFramePayloadBytes);
            BinaryPrimitives.WriteInt32LittleEndian(legacy.AsSpan(22), 1024 * 1024);
            BinaryPrimitives.WriteInt32LittleEndian(legacy.AsSpan(26), 16 * 1024 * 1024);
            legacy[38] = 1; // historical allow-response-compression flag; count/auth remain zero
            EnsureStrictHandshakePayloadRejected(legacy);
            using var negativeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await SendRejectedHandshakeAndProbeAsync(port, legacy, request, negativeTimeout.Token);
            Ensure(LegacyHandshakeDispatchProbe.InvocationCount == 0,
                "a rejected historical handshake must not dispatch its coalesced RPC bytes");
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            // Attempt every cleanup even if a preceding cleanup fails or times out.
            await ObserveCleanupAsync(() => serverCts.CancelAsync());
            await ObserveCleanupAsync(() => server.DisposeAsync().AsTask());
            await ObserveCleanupAsync(() => serverTask);
        }

        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Ensure(LegacyHandshakeDispatchProbe.InvocationCount == 0,
            "the rejected RPC must remain unexecuted after server disposal and task completion");

        async Task ObserveCleanupAsync(Func<Task> cleanup)
        {
            try
            {
                await cleanup().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception)
            {
                failure = failure is null ? exception : new AggregateException(failure, exception);
            }
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
        var serverTask = server.RunUntilStoppedAsync(serverCts.Token).AsTask();

        Exception? primary = null;
        try
        {
            using var frames = new PooledByteBufferWriter();
            var limits = new SharpLinkProtocolOptions();
            var handshake = ProtocolV2FrameWriter.BeginFrame(
                frames,
                ProtocolV2FrameType.HandshakeRequest,
                ProtocolV2FrameFlags.None,
                0);
            ProtocolV2PayloadCodec.WriteHandshakeRequest(frames, CurrentProtocolFixtureOffer(), limits);
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
        catch (Exception error) { primary = error; }
        await CleanupProtocolFixtureAsync(primary,
            () => { connection.Reader.ReleaseCompletion(); return ValueTask.CompletedTask; },
            () => new ValueTask(serverCts.CancelAsync()),
            () => server.DisposeAsync(),
            () => new ValueTask(serverTask.WaitAsync(TimeSpan.FromSeconds(2))));
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
    [NotInParallel]
    public async Task TcpUnsupportedRequiredCapabilityShouldCloseWithoutResponseOrDispatch()
    {
        LegacyHandshakeDispatchProbe.Reset();
        using var serverCts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback);
        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        var serverTask = server.RunUntilStoppedAsync(serverCts.Token).AsTask();
        Exception? primary = null;
        try
        {
            using var controlTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var request = await CreateActuallyBoundProbeRequestAsync(port, controlTimeout.Token);
            Ensure(LegacyHandshakeDispatchProbe.InvocationCount == 1, "current bound control invokes once");
            LegacyHandshakeDispatchProbe.Reset();
            using var writer = new PooledByteBufferWriter();
            // Unknown OPTIONAL support is structurally valid. Only the same bit in REQUIRED
            // is mutated below, so all other current-layout bytes remain demonstrably valid.
            const ulong unknown = 1UL << 63;
            var offer = CurrentProtocolFixtureOffer((ProtocolV2Capabilities)unknown);
            var limits = new SharpLinkProtocolOptions();
            ProtocolV2PayloadCodec.WriteHandshakeRequest(writer, offer, limits);
            var baseline = writer.WrittenMemory.ToArray();
            var decoded = ProtocolV2PayloadCodec.ReadHandshakeRequest(new(baseline), limits);
            Ensure(decoded.RequiredCapabilities == offer.RequiredCapabilities &&
                   decoded.SupportedCapabilities == offer.SupportedCapabilities && decoded.BindingLimits == offer.BindingLimits,
                "the unmutated current-layout offer must pass the strict parser");
            var malformed = (byte[])baseline.Clone();
            malformed[17] |= 0x80; // required-capabilities bytes10..17: ONLY unsupported bit63
            Ensure(malformed.Where((value, index) => value != baseline[index]).Count() == 1,
                "only the selected required-capability byte changes");
            EnsureStrictHandshakePayloadRejected(malformed);
            using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await SendRejectedHandshakeAndProbeAsync(port, malformed, request, readTimeout.Token);
            Ensure(LegacyHandshakeDispatchProbe.InvocationCount == 0, "unknown required capabilities must not dispatch RPC");
        }
        catch (Exception error) { primary = error; }
        await CleanupProtocolFixtureAsync(primary,
            () => new ValueTask(serverCts.CancelAsync()),
            () => server.DisposeAsync(),
            () => new ValueTask(serverTask.WaitAsync(TimeSpan.FromSeconds(2))));
        Ensure(LegacyHandshakeDispatchProbe.InvocationCount == 0,
            "rejected RPC remains unexecuted after endpoint teardown");
    }

    [Test]
    public async Task TcpOversizedFrameShouldFailPendingUnaryAndStreamWithSameProtocolViolation()
    {
        const int maxFramePayloadBytes = SharpLinkProtocolOptions.MinMaxFramePayloadBytes;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var fakeServerTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync(budget.Token);
            await using var stream = new NetworkStream(socket, ownsSocket: true);
            var limits = new SharpLinkProtocolOptions { MaxFramePayloadBytes = maxFramePayloadBytes };
            var request = await ReadProtocolFixtureFrameAsync(stream, limits, budget.Token);
            Ensure(request.Header.Type == ProtocolV2FrameType.HandshakeRequest && request.Header.RequestId == 0,
                "fake peer first observes the actual client handshake");
            var offer = ProtocolV2PayloadCodec.ReadHandshakeRequest(new(request.Payload), limits);
            var authority = new Protocol3BindingAuthority(Guid.NewGuid(), Guid.NewGuid(), offer.BindingLimits);
            var localManifest = GlobalCatalogManifestSource.Instance.CreateSnapshot().Single(candidate =>
                candidate.Contracts.Any(contract => contract.ContractType == typeof(ITestService)));
            var contract = localManifest.Contracts.Single(candidate => candidate.ContractType == typeof(ITestService));
            using var handshake = new PooledByteBufferWriter();
            var handshakeToken = ProtocolV2FrameWriter.BeginFrame(
                handshake, ProtocolV2FrameType.HandshakeResponse, ProtocolV2FrameFlags.None, 0);
            ProtocolV2PayloadCodec.WriteHandshakeResponse(handshake, new ProtocolV2HandshakeResponse(
                ProtocolV2Constants.MinorVersion, ProtocolV2Negotiator.MandatoryCapabilities,
                maxFramePayloadBytes, 1024 * 1024, 16 * 1024 * 1024, Authority: authority));
            ProtocolV2FrameWriter.EndFrame(handshake, handshakeToken);
            var manifestToken = ProtocolV2FrameWriter.BeginFrame(
                handshake, ProtocolV2FrameType.ContractManifest, ProtocolV2FrameFlags.None, 0);
            ProtocolV2ContractManifestCodec.Write(handshake, new ProtocolV2ContractManifest(
                1, [KeyValuePair.Create(contract.ContractId, localManifest.RpcAssemblyHash)]), limits);
            ProtocolV2FrameWriter.EndFrame(handshake, manifestToken);
            await stream.WriteAsync(handshake.WrittenMemory, budget.Token);
            await stream.FlushAsync(budget.Token);

            ulong boundToken = 0;
            var requestIds = new HashSet<ulong>();
            var unaryMethod = contract.Methods.Single(method => method.Name == nameof(ITestService.SlowAddAsync));
            var streamMethod = contract.Methods.Single(method => method.Name == nameof(ITestService.SlowDownloadAsync));
            Ensure(unaryMethod.Kind == RpcMethodKind.Unary && streamMethod.Kind == RpcMethodKind.ServerStreaming,
                "pending-call identities must retain their exact generated unary/server-streaming shapes");
            var expectedMethods = new HashSet<long> { unaryMethod.MethodId, streamMethod.MethodId };
            Ensure(expectedMethods.Count == 2, "both exact generated pending-call method IDs are declared");
            // This is a simulated peer, not a production registry. It nevertheless correlates
            // the actual Bind operation and exact generated identity before granting its token.
            while (requestIds.Count != 2)
            {
                var frame = await ReadProtocolFixtureFrameAsync(stream, limits, budget.Token);
                if (frame.Header.Type == ProtocolV2FrameType.Ping)
                {
                    await WriteProtocolFixtureFrameAsync(stream, ProtocolV2FrameType.Pong,
                        frame.Header.RequestId, frame.Payload, budget.Token);
                    continue;
                }
                if (frame.Header.Type == ProtocolV2FrameType.Pong) continue;
                if (frame.Header.Type == ProtocolV2FrameType.BindContract)
                {
                    Ensure(boundToken == 0, "fixture expects one actual binding operation");
                    var bind = Protocol3BindingControlCodec.ReadBindContract(new(frame.Payload));
                    Ensure(bind.ContractId == contract.ContractId && bind.Hash == localManifest.RpcAssemblyHash &&
                           bind.Requirement == Protocol3BindingRequirement.Compatible && bind.Receipt is null,
                        "actual Bind must match the generated contract and compatible requirement");
                    boundToken = Protocol3BindingToken.Create(0, 1, authority.Limits.Slots);
                    using var result = new PooledByteBufferWriter();
                    Protocol3BindingControlCodec.WriteBindResult(result, new Protocol3BindResult(
                        Protocol3BindStatus.Success, boundToken, bind.ContractId, bind.Hash, 1, 1,
                        authority.ServerInstanceId, authority.RegistryEpoch), authority.Limits.Slots);
                    await WriteProtocolFixtureFrameAsync(stream, ProtocolV2FrameType.BindResult,
                        frame.Header.RequestId, result.WrittenMemory, budget.Token);
                    continue;
                }
                Ensure(frame.Header.Type == ProtocolV2FrameType.Request && boundToken != 0 &&
                       frame.Payload.Length >= ProtocolV2Constants.RequestPrefixBytes &&
                       (frame.Header.Flags & ProtocolV2FrameFlags.OneWay) == 0,
                    $"both pending operations must be actual non-OneWay bound requests; type={frame.Header.Type}, flags={frame.Header.Flags}");
                Ensure(BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload) == boundToken,
                    "the request must carry the confirmed physical-session binding token");
                var methodId = BinaryPrimitives.ReadInt64LittleEndian(frame.Payload.AsSpan(sizeof(long)));
                var hasReturn = (frame.Header.Flags & ProtocolV2FrameFlags.HasReturn) != 0;
                var cancellable = (frame.Header.Flags & ProtocolV2FrameFlags.Cancellable) != 0;
                // HasReturn denotes a single return payload, not the existence of a response
                // stream. The actual server-streaming sender uses Cancellable without HasReturn.
                Ensure(methodId == unaryMethod.MethodId && hasReturn ||
                       methodId == streamMethod.MethodId && !hasReturn && cancellable,
                    $"request flags must match the exact generated method; method={methodId}, flags={frame.Header.Flags}");
                Ensure(expectedMethods.Remove(methodId) && requestIds.Add(frame.Header.RequestId),
                    "observe one distinct actual request for each generated pending method");
            }

            // Deliberate malformed header and first-failure oracle are unchanged; the gate
            // above replaces the old time delay so BOTH actual operations are pending first.
            var maliciousHeader = new byte[ProtocolV2Constants.HeaderBytes];
            maliciousHeader[0] = ProtocolV2Constants.Magic;
            BinaryPrimitives.WriteInt32LittleEndian(
                maliciousHeader.AsSpan(1, sizeof(int)), maxFramePayloadBytes + 1);
            maliciousHeader[5] = (byte)ProtocolV2FrameType.Response;
            BinaryPrimitives.WriteUInt64LittleEndian(maliciousHeader.AsSpan(7, sizeof(ulong)), 1);
            await stream.WriteAsync(maliciousHeader, budget.Token);
            await stream.FlushAsync(budget.Token);
        });

        ISharpLinkClient? client = null;
        Task? unaryTask = null;
        Task? streamTask = null;
        SharpLinkException? unaryError = null;
        SharpLinkException? streamError = null;
        Exception? primary = null;
        try
        {
            client = SharpClientBuilder.Create().DisableRequestTimeout()
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseProtocol(static options => options.MaxFramePayloadBytes = maxFramePayloadBytes)
                .UseHeartbeat(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)).Build();
            await client.ConnectAsync(budget.Token);
            Ensure(client.State == SharpLinkConnectionState.Ready, "fake server handshake");
            var svcProxy = client.CreateProxy<ITestService>();
            await svcProxy.PrepareAsync(TimeSpan.FromSeconds(5));
            var svc = svcProxy.Service;
            unaryTask = svc.SlowAddAsync(1, 2, CancellationToken.None).AsTask();
            streamTask = CollectAsync(
                svc.SlowDownloadAsync(100, 200, CancellationToken.None), CancellationToken.None);
            unaryError = await CaptureSharpLinkException(unaryTask, "oversized frame unary");
            streamError = await CaptureSharpLinkException(streamTask, "oversized frame stream");
            await fakeServerTask;
            Ensure(unaryError.Code == SharpLinkErrorCode.ProtocolViolation, "unary protocol violation");
            Ensure(streamError.Code == SharpLinkErrorCode.ProtocolViolation, "stream protocol violation");
            Ensure(ReferenceEquals(unaryError, streamError), "pending operations should receive the first failure instance");
        }
        catch (Exception error) { primary = error; }
        await CleanupProtocolFixtureAsync(primary,
            () => client?.DisposeAsync() ?? ValueTask.CompletedTask,
            () => new ValueTask(budget.CancelAsync()),
            () => { listener.Stop(); return ValueTask.CompletedTask; },
            async () =>
            {
                try { await fakeServerTask.WaitAsync(TimeSpan.FromSeconds(1)); }
                catch (OperationCanceledException) when (budget.IsCancellationRequested) { }
            },
            () => JoinProtocolFixtureCallAsync(unaryTask, unaryError),
            () => JoinProtocolFixtureCallAsync(streamTask, streamError));
    }

    private static ProtocolV2HandshakeRequest CurrentProtocolFixtureOffer(
        ProtocolV2Capabilities optional = ProtocolV2Capabilities.None)
        => new(ProtocolV2Constants.MinorVersion,
            ProtocolV2Negotiator.MandatoryCapabilities | optional, ProtocolV2Negotiator.MandatoryCapabilities,
            SharpLinkProtocolOptions.DefaultMaxFramePayloadBytes, 1024 * 1024, 16 * 1024 * 1024,
            ReadOnlyMemory<byte>.Empty, BindingLimits: Protocol3BindingLimits.Default);

    private static async Task<(ProtocolV2FrameHeader Header, byte[] Payload)> ReadProtocolFixtureFrameAsync(
        NetworkStream stream, SharpLinkProtocolOptions limits, CancellationToken cancellationToken)
    {
        var headerBytes = new byte[ProtocolV2Constants.HeaderBytes];
        await stream.ReadExactlyAsync(headerBytes, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.AsSpan(1));
        Ensure(length >= 0 && length <= limits.MaxFramePayloadBytes, "fixture incoming frame length is bounded");
        var bytes = new byte[ProtocolV2Constants.HeaderBytes + length];
        headerBytes.CopyTo(bytes, 0);
        await stream.ReadExactlyAsync(bytes.AsMemory(ProtocolV2Constants.HeaderBytes), cancellationToken);
        var sequence = new ReadOnlySequence<byte>(bytes);
        Ensure(ProtocolV2FrameParser.TryReadFrame(ref sequence, limits, out var header, out var payload) && sequence.IsEmpty,
            "fixture reads one complete structurally valid frame");
        return (header, payload.ToArray());
    }

    private static async Task WriteProtocolFixtureFrameAsync(NetworkStream stream,
        ProtocolV2FrameType type, ulong requestId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        using var writer = new PooledByteBufferWriter();
        var token = ProtocolV2FrameWriter.BeginFrame(writer, type, ProtocolV2FrameFlags.None, requestId);
        writer.Write(payload.Span);
        ProtocolV2FrameWriter.EndFrame(writer, token);
        await stream.WriteAsync(writer.WrittenMemory, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<byte[]> CreateActuallyBoundProbeRequestAsync(int port, CancellationToken cancellationToken)
    {
        var manifest = GlobalCatalogManifestSource.Instance.CreateSnapshot().Single(candidate =>
            candidate.Contracts.Any(contract => contract.ContractType == typeof(ILegacyHandshakeDispatchProbe)));
        var contract = manifest.Contracts.Single(contract => contract.ContractType == typeof(ILegacyHandshakeDispatchProbe));
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        var limits = new SharpLinkProtocolOptions();
        using var offerPayload = new PooledByteBufferWriter();
        ProtocolV2PayloadCodec.WriteHandshakeRequest(offerPayload, CurrentProtocolFixtureOffer(), limits);
        await WriteProtocolFixtureFrameAsync(stream, ProtocolV2FrameType.HandshakeRequest, 0,
            offerPayload.WrittenMemory, cancellationToken);
        var handshake = await ReadProtocolFixtureFrameAsync(stream, limits, cancellationToken);
        Ensure(handshake.Header.Type == ProtocolV2FrameType.HandshakeResponse &&
               handshake.Header.Flags == ProtocolV2FrameFlags.None && handshake.Header.RequestId == 0,
            "current control handshake succeeds");
        var negotiated = ProtocolV2PayloadCodec.ReadHandshakeResponse(new(handshake.Payload), limits);
        var manifestFrame = await ReadProtocolFixtureFrameAsync(stream, limits, cancellationToken);
        Ensure(manifestFrame.Header.Type == ProtocolV2FrameType.ContractManifest && manifestFrame.Header.RequestId == 0,
            "current control receives mandatory bootstrap locator");
        var locator = ProtocolV2ContractManifestCodec.Read(new(manifestFrame.Payload), limits);
        Ensure(locator.Generation == 1 && locator.OrderedContracts.Any(pair =>
            pair.Key == contract.ContractId && pair.Value == manifest.RpcAssemblyHash),
            "current control locator names the exact actual generated contract at initial generation");
        var bind = new Protocol3BindRequest(Protocol3BindingRequirement.Compatible, contract.ContractId, manifest.RpcAssemblyHash);
        using var bindPayload = new PooledByteBufferWriter();
        Protocol3BindingControlCodec.WriteBindContract(bindPayload, bind);
        await WriteProtocolFixtureFrameAsync(stream, ProtocolV2FrameType.BindContract, 1,
            bindPayload.WrittenMemory, cancellationToken);
        var bound = await ReadProtocolFixtureFrameAsync(stream, limits, cancellationToken);
        Ensure(bound.Header.Type == ProtocolV2FrameType.BindResult && bound.Header.RequestId == 1,
            "actual Bind result is correlated to the current operation");
        var result = Protocol3BindingControlCodec.ReadBindResult(new(bound.Payload), negotiated.Authority.Limits.Slots);
        Protocol3BindingValidation.ValidateResultForRequest(result, bind, negotiated.Authority, negotiated.Authority.Limits.Slots);
        Ensure(result.Status == Protocol3BindStatus.Success, "actual server must allocate a successful binding");
        using var request = new PooledByteBufferWriter();
        var requestToken = ProtocolV2FrameWriter.BeginFrame(request,
            ProtocolV2FrameType.Request, ProtocolV2FrameFlags.HasReturn, 1);
        var prefix = new byte[ProtocolV2Constants.RequestPrefixBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(prefix, result.Token);
        BinaryPrimitives.WriteInt64LittleEndian(prefix.AsSpan(sizeof(long)), contract.Methods.Single().MethodId);
        request.Write(prefix);
        ProtocolV2FrameWriter.EndFrame(request, requestToken);
        await stream.WriteAsync(request.WrittenMemory, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        var response = await ReadProtocolFixtureFrameAsync(stream, limits, cancellationToken);
        Ensure(response.Header.Type == ProtocolV2FrameType.Response && response.Header.RequestId == 1 &&
               (response.Header.Flags & ProtocolV2FrameFlags.Error) == 0,
            "actual bound current-revision RPC returns a successful response");
        return request.WrittenMemory.ToArray();
    }

    private static void EnsureStrictHandshakePayloadRejected(byte[] payload)
    {
        try { ProtocolV2PayloadCodec.ReadHandshakeRequest(new(payload), new SharpLinkProtocolOptions()); }
        catch (SharpLinkProtocolViolationException error) when (error.Reason == ProtocolViolationReason.MalformedFrame) { return; }
        throw new Exception("The deliberately malformed wire payload must fail the strict current parser.");
    }

    private static async Task SendRejectedHandshakeAndProbeAsync(int port, byte[] handshake,
        byte[] previouslyValidRequest, CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        using var frames = new PooledByteBufferWriter();
        var token = ProtocolV2FrameWriter.BeginFrame(frames,
            ProtocolV2FrameType.HandshakeRequest, ProtocolV2FrameFlags.None, 0);
        frames.Write(handshake);
        ProtocolV2FrameWriter.EndFrame(frames, token);
        // Same request bytes proved executable by the positive control. Its token belongs
        // ONLY to that earlier physical session; rejection here must precede RPC admission.
        frames.Write(previouslyValidRequest);
        await stream.WriteAsync(frames.WrittenMemory, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        Ensure(await stream.ReadAsync(new byte[1], cancellationToken) == 0,
            "strict malformed-handshake rejection closes with EOF and sends no response bytes");
    }

    private static async ValueTask JoinProtocolFixtureCallAsync(Task? operation, SharpLinkException? observed)
    {
        if (operation is null) return;
        try { await operation.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (SharpLinkException error) when (observed is { Code: SharpLinkErrorCode.ProtocolViolation } &&
                                               ReferenceEquals(error, observed))
        {
            // This exact logical outcome was already captured by the test's code/identity
            // oracle. Do not turn an expected failing RPC into a new teardown failure.
        }
    }

    private static async ValueTask CleanupProtocolFixtureAsync(Exception? primary, params Func<ValueTask>[] stages)
    {
        List<Exception>? failures = primary is null ? null : [primary];
        foreach (var stage in stages)
        {
            try { await stage().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null) throw new AggregateException("Protocol fixture body/cleanup failures.", failures);
    }

}

[RpcContract]
public interface ILegacyHandshakeDispatchProbe : IService
{
    [NonCancellable]
    ValueTask<int> TouchAsync();
}

[RpcService]
public sealed class LegacyHandshakeDispatchProbe : ILegacyHandshakeDispatchProbe
{
    private static int s_invocationCount;
    internal static int InvocationCount => Volatile.Read(ref s_invocationCount);
    internal static void Reset() => Interlocked.Exchange(ref s_invocationCount, 0);
    public ValueTask<int> TouchAsync() => ValueTask.FromResult(Interlocked.Increment(ref s_invocationCount));
}
