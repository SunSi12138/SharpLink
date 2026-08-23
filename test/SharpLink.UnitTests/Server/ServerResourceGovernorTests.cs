using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using System.Threading;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public class ServerResourceGovernorTests
{
    [Test]
    public async Task DecodeResourcesShouldRemainBoundedAndRequestOwned()
    {
        await using var server = CreateServer(
            maxCalls: 2,
            maxDecodes: 2,
            retainedBytes: 1024,
            decodedBytes: 2048);
        await using var session = CreateSession("resource-governor-bounds");
        var connection = CreateConnection(session, maxConcurrentCalls: 2);
        Ensure(connection.MarkReady(null), "connection ready");
        SetServerState(server, 2); // Running

        SharpLinkServer.ServerRequestPermit? firstRequest = null;
        SharpLinkServer.ServerRequestPermit? secondRequest = null;
        try
        {
            Ensure(server.TryReserveCall(connection, out firstRequest) ==
                   SharpLinkServer.ServerCallAdmissionResult.Acquired && firstRequest is not null,
                "first call reservation");
            Ensure(server.TryReserveCall(connection, out secondRequest) ==
                   SharpLinkServer.ServerCallAdmissionResult.Acquired && secondRequest is not null,
                "second call reservation");
            var first = firstRequest!;
            var second = secondRequest!;

            Ensure(first.TryAcquireDecodePermit(800, out var firstDecode) && firstDecode is not null,
                "first decode permit");
            var firstDecodePermit = firstDecode!;
            Ensure(server.ActiveDecodeCountForDiagnostics == 1 &&
                   server.RetainedCompressedBytesForDiagnostics == 800,
                "first decode must own one concurrency credit and its retained bytes");

            Ensure(!second.TryAcquireDecodePermit(300, out var rejectedDecode) && rejectedDecode is null,
                "retained-byte budget must reject the second decode without attaching a permit");
            Ensure(server.ActiveDecodeCountForDiagnostics == 1 &&
                   server.RetainedCompressedBytesForDiagnostics == 800,
                "failed retained-byte acquisition must roll back its provisional decode credit");

            Ensure(second.TryAcquireDecodePermit(224, out var secondDecode) && secondDecode is not null,
                "the exact remaining retained-byte budget must be reusable after rollback");
            var secondDecodePermit = secondDecode!;
            Ensure(server.ActiveDecodeCountForDiagnostics == 2 &&
                   server.RetainedCompressedBytesForDiagnostics == 1024,
                "both successful decodes must be accounted");

            Ensure(firstDecodePermit.TryReserveDecodedBytes(1536),
                "first decoded-byte reservation");
            Ensure(!secondDecodePermit.TryReserveDecodedBytes(600),
                "decoded-byte budget must reject an over-budget rent");
            Ensure(server.DecodedBytesInFlightForDiagnostics == 1536,
                "failed decoded-byte reservation must leave accounting unchanged");
            Ensure(secondDecodePermit.TryReserveDecodedBytes(512),
                "the exact remaining decoded-byte budget must be admitted");
            Ensure(server.DecodedBytesInFlightForDiagnostics == 2048,
                "successful decoded ownership must fill the configured budget exactly");

            var prematureActivation = CaptureFailure(first.Activate);
            Ensure(prematureActivation is InvalidOperationException,
                "a request with attached decode resources must not activate before decode completion");

            firstDecodePermit.CompleteDecode();
            Ensure(server.ActiveDecodeCountForDiagnostics == 1 &&
                   server.RetainedCompressedBytesForDiagnostics == 224 &&
                   server.DecodedBytesInFlightForDiagnostics == 2048,
                "CompleteDecode must release only CPU/retained ownership, not decoded bytes");
            first.Activate();

            second.Dispose();
            secondRequest = null;
            Ensure(server.ActiveDecodeCountForDiagnostics == 0 &&
                   server.RetainedCompressedBytesForDiagnostics == 0 &&
                   server.DecodedBytesInFlightForDiagnostics == 1536,
                "disposing a still-decoding request must release its attached decode/retained/decoded resources");

            first.Dispose();
            firstRequest = null;
            Ensure(server.ActiveDecodeCountForDiagnostics == 0 &&
                   server.RetainedCompressedBytesForDiagnostics == 0 &&
                   server.DecodedBytesInFlightForDiagnostics == 0,
                "final request disposal must release decoded-byte ownership exactly once");
            Ensure(server.ActiveCallCountForDiagnostics == 0 && connection.ActiveCalls == 0,
                "resource cleanup must leave both call-capacity scopes reusable");
        }
        finally
        {
            secondRequest?.Dispose();
            firstRequest?.Dispose();
            SetServerState(server, 3); // Draining
            await connection.CloseAsync();
            await connection.ServiceCleanupTask;
        }
    }

    [Test]
    public async Task DecodeConcurrencyShouldRejectWithoutRetainedOrDecodedSideEffects()
    {
        await using var server = CreateServer(
            maxCalls: 2,
            maxDecodes: 1,
            retainedBytes: 1024,
            decodedBytes: 1024);
        await using var session = CreateSession("resource-governor-decode-credit");
        var connection = CreateConnection(session, maxConcurrentCalls: 2);
        Ensure(connection.MarkReady(null), "connection ready");
        SetServerState(server, 2); // Running

        SharpLinkServer.ServerRequestPermit? firstRequest = null;
        SharpLinkServer.ServerRequestPermit? secondRequest = null;
        try
        {
            Ensure(server.TryReserveCall(connection, out firstRequest) ==
                   SharpLinkServer.ServerCallAdmissionResult.Acquired && firstRequest is not null,
                "first call reservation");
            Ensure(server.TryReserveCall(connection, out secondRequest) ==
                   SharpLinkServer.ServerCallAdmissionResult.Acquired && secondRequest is not null,
                "second call reservation");
            var first = firstRequest!;
            var second = secondRequest!;

            Ensure(first.TryAcquireDecodePermit(512, out var firstDecode) && firstDecode is not null,
                "first decode permit");
            var firstDecodePermit = firstDecode!;

            Ensure(!second.TryAcquireDecodePermit(256, out var rejectedDecode) && rejectedDecode is null,
                "decode-concurrency exhaustion must reject before retained ownership");
            Ensure(server.ActiveDecodeCountForDiagnostics == 1 &&
                   server.RetainedCompressedBytesForDiagnostics == 512 &&
                   server.DecodedBytesInFlightForDiagnostics == 0,
                "decode-credit rejection must not mutate retained or decoded-byte accounting");

            firstDecodePermit.CompleteDecode();
            Ensure(second.TryAcquireDecodePermit(256, out var secondDecode) && secondDecode is not null,
                "decode credit must be reusable immediately after CompleteDecode");
            var secondDecodePermit = secondDecode!;
            Ensure(secondDecodePermit.TryReserveDecodedBytes(1024), "decoded-byte reservation");

            second.Dispose();
            secondRequest = null;
            first.Dispose();
            firstRequest = null;
            Ensure(server.ActiveDecodeCountForDiagnostics == 0 &&
                   server.RetainedCompressedBytesForDiagnostics == 0 &&
                   server.DecodedBytesInFlightForDiagnostics == 0,
                "all resource accounting must return to zero");
        }
        finally
        {
            secondRequest?.Dispose();
            firstRequest?.Dispose();
            SetServerState(server, 3); // Draining
            await connection.CloseAsync();
            await connection.ServiceCleanupTask;
        }
    }

    [Test]
    public void DecodeResourceOptionsShouldValidateHardBounds()
    {
        var invalidDecodeCount = CaptureFailure(new SharpLinkFlowControlOptions
        {
            MaxConcurrentDecodesPerServer = 0
        }.Validate);
        var invalidRetainedBudget = CaptureFailure(new SharpLinkFlowControlOptions
        {
            MaxRetainedCompressedBytesPerServer = 0
        }.Validate);
        var invalidDecodedBudget = CaptureFailure(new SharpLinkFlowControlOptions
        {
            MaxDecodedBytesInFlightPerServer = 0
        }.Validate);

        Ensure(invalidDecodeCount is ArgumentOutOfRangeException,
            "decode concurrency must have a positive hard bound");
        Ensure(invalidRetainedBudget is ArgumentOutOfRangeException,
            "retained compressed bytes must have a positive hard bound");
        Ensure(invalidDecodedBudget is ArgumentOutOfRangeException,
            "decoded bytes in flight must have a positive hard bound");
    }

    private static SharpLinkServer CreateServer(
        int maxCalls,
        int maxDecodes,
        long retainedBytes,
        long decodedBytes)
        => (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseRuntime(options =>
            {
                options.FlowControl.MaxConcurrentCallsPerConnection = maxCalls;
                options.FlowControl.MaxConcurrentCallsPerServer = maxCalls;
                options.FlowControl.MaxConcurrentDecodesPerServer = maxDecodes;
                options.FlowControl.MaxRetainedCompressedBytesPerServer = retainedBytes;
                options.FlowControl.MaxDecodedBytesInFlightPerServer = decodedBytes;
            })
            .UseTransport(new IdleListener())
            .Build();

    private static RpcSession CreateSession(string id)
    {
        var input = new Pipe();
        var output = new Pipe();
        return RpcSessionTestFixture.CreateSessionOverTestTransport(
            id,
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ServerOptions());
    }

    private static ServerConnectionState CreateConnection(RpcSession session, int maxConcurrentCalls)
        => new(
            session,
            new RpcSessionGeneratedServerBridge(session),
            new StripedLongMap<ServerCallCancellationState>(),
            CancellationToken.None,
            TimeProvider.System,
            maxConcurrentCalls);

    private static void SetServerState(SharpLinkServer server, int state)
    {
        var field = typeof(SharpLinkServer).GetField(
            "_state",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find server lifecycle state");
        field.SetValue(server, state);
    }

    private static Exception? CaptureFailure(Action action)
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

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class IdleListener : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => null;

        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new OperationCanceledException(cancellationToken));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
