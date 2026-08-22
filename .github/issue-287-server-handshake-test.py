from pathlib import Path

p = Path('test/SharpLink.IntegrationTests/TransportConnectionIntegrationTests.cs')
text = p.read_text()
marker = '''    [Test]
    [NotInParallel]
    public async Task ServerProtocolViolationShouldReleaseItsReadBeforeCompletingTheReader()
'''
test = r'''    [Test]
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

'''
assert marker in text
text = text.replace(marker, test + marker)
p.write_text(text)
