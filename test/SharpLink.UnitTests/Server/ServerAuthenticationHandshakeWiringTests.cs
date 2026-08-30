using System.IO.Pipelines;
using System.Net;
using System.Threading.Channels;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public class ServerAuthenticationHandshakeWiringTests
{
    [Test]
    public async Task HandshakeShouldForwardConnectionIdentityPayloadAndEndpointsToAuthenticator()
    {
        var observedRequest = new TaskCompletionSource<SharpLinkAuthenticationRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var authenticator = SharpLinkAuthenticator.CreateServer((request, cancellationToken) =>
        {
            observedRequest.TrySetResult(request);
            return ValueTask.FromResult(SharpLinkAuthenticationResult.Success);
        });
        var listener = new ScriptedListener();
        await using var harness = await StartServerAsync(listener, authenticator);

        const string connectionId = "auth-wiring";
        byte[] authenticationPayload = [0x01, 0x7F, 0xA5, 0x5C];
        var localEndPoint = new IPEndPoint(IPAddress.Loopback, 43123);
        var remoteEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 53214);
        var connection = new TestConnection(
            connectionId,
            localEndPoint,
            remoteEndPoint);

        listener.Enqueue(connection);
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveHandshakes == 1,
            "the connection must hold the handshake slot before the request is written");
        WriteValidHandshakeRequest(
            connection.FeedInput,
            new SharpLinkProtocolOptions(),
            authenticationPayload);

        var request = await observedRequest.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Ensure(request.ConnectionId == connectionId, "the transport connection id must reach the authenticator unchanged");
        Ensure(
            request.Payload.Span.SequenceEqual(authenticationPayload),
            "the opaque handshake authentication payload must reach the authenticator unchanged");
        Ensure(
            Equals(request.LocalEndPoint, localEndPoint),
            "the transport local endpoint must reach the authenticator unchanged");
        Ensure(
            Equals(request.RemoteEndPoint, remoteEndPoint),
            "the transport remote endpoint must reach the authenticator unchanged");
    }

    private static async Task<ServerHarness> StartServerAsync(
        ScriptedListener listener,
        ISharpLinkServerAuthenticator authenticator)
    {
        var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTransport(listener)
            .UseAuthenticator(authenticator)
            .Build();
        var runCts = new CancellationTokenSource();
        var runTask = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(runCts.Token);
            }
            catch (OperationCanceledException) when (runCts.IsCancellationRequested)
            {
            }
        }, runCts.Token);
        await YieldUntilAsync(
            () => server.HealthStatus == SharpLinkHealthStatus.Ready,
            "the scripted server must reach Running");
        return new ServerHarness(server, runTask, runCts);
    }

    private static void WriteValidHandshakeRequest(
        PipeWriter output,
        SharpLinkProtocolOptions limits,
        ReadOnlyMemory<byte> authenticationPayload)
    {
        var writer = new PooledByteBufferWriter();
        var token = ProtocolV2FrameWriter.BeginFrame(
            writer,
            ProtocolV2FrameType.HandshakeRequest,
            ProtocolV2FrameFlags.None,
            0);
        var request = new ProtocolV2HandshakeRequest(
            ProtocolV2Constants.MinorVersion,
            ProtocolV2Capabilities.None,
            ProtocolV2Capabilities.None,
            limits.MaxFramePayloadBytes,
            1024 * 1024,
            16 * 1024 * 1024,
            authenticationPayload,
            ReadOnlyMemory<string>.Empty);
        ProtocolV2PayloadCodec.WriteHandshakeRequest(writer, request, limits);
        ProtocolV2FrameWriter.EndFrame(writer, token);
        output.Write(writer.WrittenMemory.ToArray());
        output.FlushAsync().AsTask().GetAwaiter().GetResult();
    }

    private static async Task YieldUntilAsync(
        Func<bool> condition,
        string failureMessage,
        int attempts = 2000)
    {
        var deadline = Environment.TickCount64 + 15000;
        for (var attempt = 0; attempt < attempts && !condition(); attempt++)
        {
            if (Environment.TickCount64 >= deadline)
                break;
            if (attempt % 32 == 0)
                await Task.Delay(1);
            else
                await Task.Yield();
        }
        Ensure(condition(), failureMessage);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class ScriptedListener : IServerTransportListener
    {
        private readonly Channel<ITransportConnection> _channel =
            Channel.CreateUnbounded<ITransportConnection>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

        public EndPoint? LocalEndPoint => null;

        public async ValueTask<ITransportConnection> AcceptAsync(
            CancellationToken cancellationToken = default)
            => await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        public ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        internal void Enqueue(ITransportConnection connection)
        {
            if (!_channel.Writer.TryWrite(connection))
                throw new InvalidOperationException("The scripted listener was already disposed.");
        }
    }

    private sealed class TestConnection : ITransportConnection
    {
        private readonly Pipe _inputPipe = new();
        private readonly Pipe _outputPipe = new();
        private int _disposeCount;

        internal TestConnection(
            string id,
            EndPoint? localEndPoint,
            EndPoint? remoteEndPoint)
        {
            Id = id;
            LocalEndPoint = localEndPoint;
            RemoteEndPoint = remoteEndPoint;
        }

        public string Id { get; }

        public PipeReader Input => _inputPipe.Reader;

        public PipeWriter Output => _outputPipe.Writer;

        public EndPoint? LocalEndPoint { get; }

        public EndPoint? RemoteEndPoint { get; }

        internal PipeWriter FeedInput => _inputPipe.Writer;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            await CompleteAsync(_inputPipe.Writer);
            await CompleteAsync(_outputPipe.Writer);
            await CompleteAsync(_inputPipe.Reader);
            await CompleteAsync(_outputPipe.Reader);
        }

        private static async ValueTask CompleteAsync(PipeWriter writer)
        {
            try
            {
                await writer.CompleteAsync();
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static async ValueTask CompleteAsync(PipeReader reader)
        {
            try
            {
                await reader.CompleteAsync();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private sealed class ServerHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _runCts;
        private bool _disposed;

        internal ServerHarness(
            SharpLinkServer server,
            Task runTask,
            CancellationTokenSource runCts)
        {
            Server = server;
            RunTask = runTask;
            _runCts = runCts;
        }

        internal SharpLinkServer Server { get; }

        internal Task RunTask { get; }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;

            try
            {
                await Server.StopAsync(TimeSpan.Zero);
            }
            catch
            {
            }
            _runCts.Cancel();
            try
            {
                await RunTask;
            }
            catch
            {
            }
            _runCts.Dispose();
            try
            {
                await Server.DisposeAsync();
            }
            catch
            {
            }
        }
    }
}
