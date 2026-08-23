namespace SharpLink.IntegrationTests;

public class MalformedRequestProtocolIntegrationTests
{
    [Test]
    public async Task TcpMalformedRequestShouldTerminateConnection()
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
                    ProtocolV2Capabilities.None,
                    ProtocolV2Capabilities.None,
                    SharpLinkProtocolOptions.DefaultMaxFramePayloadBytes,
                    1024 * 1024,
                    16 * 1024 * 1024,
                    ReadOnlyMemory<byte>.Empty),
                limits);
            ProtocolV2FrameWriter.EndFrame(frames, handshakeToken);

            var requestToken = ProtocolV2FrameWriter.BeginFrame(
                frames,
                ProtocolV2FrameType.Request,
                ProtocolV2FrameFlags.None,
                1);
            // A request routing prefix requires both interface and method hashes (16 bytes).
            frames.Write(new byte[sizeof(long)]);
            ProtocolV2FrameWriter.EndFrame(frames, requestToken);

            await stream.WriteAsync(frames.WrittenMemory);
            await stream.FlushAsync();

            var received = new byte[4096];
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var firstRead = await stream.ReadAsync(received, readCts.Token);
            if (firstRead <= 0)
            {
                throw new Exception(
                    "Valid handshake should complete before the malformed Request terminates the connection.");
            }

            while (await stream.ReadAsync(received, readCts.Token) != 0)
            {
            }
        }
        finally
        {
            await serverCts.CancelAsync();
            await server.DisposeAsync();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }
}
