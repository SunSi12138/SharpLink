using SharpLink.Server;
using SharpLink.UnitTests.Runtime;
using System.IO.Pipelines;
using System.Reflection;
using System.Threading;

namespace SharpLink.UnitTests.Server;

public class ServerDecodeResponseBackpressureTests
{
    [Test]
    public async Task DecodeResourcesShouldReleaseWhileErrorResponseRemainsBackpressured()
    {
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseRuntime(options =>
            {
                options.FlowControl.MaxConcurrentCallsPerConnection = 1;
                options.FlowControl.MaxConcurrentCallsPerServer = 1;
                options.FlowControl.MaxConcurrentDecodesPerServer = 1;
                options.FlowControl.MaxRetainedCompressedBytesPerServer = 1024;
                options.FlowControl.MaxDecodedBytesInFlightPerServer = 1024;
            })
            .UseTransport(new IdleListener())
            .Build();
        var runtimeContext = (SharpLinkRuntimeContext)(
            typeof(SharpLinkServer).GetField(
                "_runtimeContext",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(server)!);
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "decode-response-backpressure",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ServerOptions(runtimeContext));
        var callCancellations = new StripedLongMap<ServerCallCancellationState>(runtimeContext.Concurrency);
        var connection = new ServerConnectionState(
            session,
            new RpcSessionGeneratedServerBridge(session),
            callCancellations,
            CancellationToken.None,
            runtimeContext.TimeProvider,
            maxConcurrentCalls: 1);
        Ensure(connection.MarkReady(null), "connection ready");
        typeof(SharpLinkServer).GetField(
                "_state",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(server, 2); // Running

        var admission = server.TryReserveCall(connection, out var requestPermit);
        Ensure(admission == ServerCallAdmissionResult.Acquired && requestPermit is not null,
            "request permit acquired");
        var permit = requestPermit ?? throw new Exception("request permit was not returned");
        Ensure(permit.TryAcquireDecodePermit(128, out var decodePermit) && decodePermit is not null,
            "decode permit acquired");
        var decode = decodePermit ?? throw new Exception("decode permit was not returned");
        Ensure(decode.TryReserveDecodedBytes(256), "decoded-byte budget acquired");
        Ensure(server.ActiveDecodeCountForDiagnostics == 1 &&
               server.RetainedCompressedBytesForDiagnostics == 128 &&
               server.DecodedBytesInFlightForDiagnostics == 256,
            "decode ownership established");

        var responseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAfterResponse = typeof(SharpLinkServer).GetMethod(
            "ReleaseDispatchResourcesAfterResponseAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find response-completion release helper");
        var responseRelease = (ValueTask)releaseAfterResponse.Invoke(server,
        [
            new ValueTask(responseGate.Task),
            null,
            71L,
            callCancellations,
            connection,
            permit
        ])!;
        Ensure(!responseRelease.IsCompleted && server.ActiveCallCountForDiagnostics == 1 && connection.ActiveCalls == 1,
            "pending response must retain call capacity");

        permit.ReleaseDecodeResources();

        Ensure(!responseRelease.IsCompleted,
            "decode sub-ownership release must not complete the pending response");
        Ensure(server.ActiveDecodeCountForDiagnostics == 0 &&
               server.RetainedCompressedBytesForDiagnostics == 0 &&
               server.DecodedBytesInFlightForDiagnostics == 0,
            "pending response must not retain decode or byte budgets");
        Ensure(server.ActiveCallCountForDiagnostics == 1 && connection.ActiveCalls == 1,
            "call capacity remains tied to response completion");

        responseGate.TrySetResult();
        await responseRelease.AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(server.ActiveCallCountForDiagnostics == 0 && connection.ActiveCalls == 0,
            "response completion releases call capacity");

        await connection.CloseAsync();
        await input.Writer.CompleteAsync();
    }

    [Test]
    [NotInParallel]
    public async Task DecodedByteAccountingShouldFollowDeferredPayloadOwnerReturn()
    {
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseRuntime(options =>
            {
                options.FlowControl.MaxConcurrentCallsPerConnection = 1;
                options.FlowControl.MaxConcurrentCallsPerServer = 1;
                options.FlowControl.MaxConcurrentDecodesPerServer = 1;
                options.FlowControl.MaxRetainedCompressedBytesPerServer = 1024;
                options.FlowControl.MaxDecodedBytesInFlightPerServer = 1024;
            })
            .UseTransport(new IdleListener())
            .Build();
        var runtimeContext = (SharpLinkRuntimeContext)(
            typeof(SharpLinkServer).GetField(
                "_runtimeContext",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(server)!);
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "decoded-byte-external-lease",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ServerOptions(runtimeContext));
        var callCancellations = new StripedLongMap<ServerCallCancellationState>(runtimeContext.Concurrency);
        var connection = new ServerConnectionState(
            session,
            new RpcSessionGeneratedServerBridge(session),
            callCancellations,
            CancellationToken.None,
            runtimeContext.TimeProvider,
            maxConcurrentCalls: 1);
        Ensure(connection.MarkReady(null), "connection ready");
        typeof(SharpLinkServer).GetField(
                "_state",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(server, 2); // Running

        var admission = server.TryReserveCall(connection, out var requestPermit);
        Ensure(admission == ServerCallAdmissionResult.Acquired && requestPermit is not null,
            "request permit acquired");
        var permit = requestPermit ?? throw new Exception("request permit was not returned");
        Ensure(permit.TryAcquireDecodePermit(0, out var decodePermit) && decodePermit is not null,
            "decode permit acquired");
        var decode = decodePermit ?? throw new Exception("decode permit was not returned");
        Ensure(decode.TryReserveDecodedBytes(256), "decoded-byte budget acquired");
        decode.CompleteDecode();
        permit.Activate();
        Ensure(server.ActiveDecodeCountForDiagnostics == 0 &&
               server.DecodedBytesInFlightForDiagnostics == 256,
            "decoded-byte ownership survives decode completion");

        var payloadOwner = runtimeContext.Buffers.Rent(256);
        payloadOwner.GetSpan(256)[..256].Fill(0x2A);
        payloadOwner.Advance(256);
        var callState = ServerCallCancellationState.Rent(
            72,
            default,
            runtimeContext.TimeProvider,
            CancellationToken.None,
            CancellationToken.None,
            supportsCooperativeCancellation: false);
        callState.AttachPayloadOwner(runtimeContext.Buffers, payloadOwner);
        callCancellations.Set(72, callState);
        var externalLease = callState.CaptureLease(72);
        Ensure(externalLease.TryAcquire(), "external call-state lease acquired");
        var externalUseOwned = true;
        var dispatchTeardownOwned = false;

        try
        {
            var releaseDispatch = typeof(SharpLinkServer).GetMethod(
                "ReleaseDispatchResources",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new Exception("cannot find dispatch resource release helper");
            releaseDispatch.Invoke(server,
            [
                callState,
                72L,
                callCancellations,
                connection,
                permit
            ]);
            dispatchTeardownOwned = true;

            Ensure(server.ActiveCallCountForDiagnostics == 0 && connection.ActiveCalls == 0,
                "dispatch teardown releases call capacity even while the call-state lease is retained");
            Ensure(callState.HasPayloadOwnerForDiagnostics,
                "external call-state lease must keep the physical decoded payload owner alive");
            Ensure(server.DecodedBytesInFlightForDiagnostics == 256,
                "decoded-byte accounting must remain charged while the physical owner is retained");

            externalLease.ReleaseUse();
            externalUseOwned = false;

            Ensure(server.DecodedBytesInFlightForDiagnostics == 0,
                "returning the physical decoded payload must release its decoded-byte accounting");
        }
        finally
        {
            if (externalUseOwned)
                externalLease.ReleaseUse();
            if (!dispatchTeardownOwned)
            {
                permit.Dispose();
                callState.Dispose();
            }
        }

        await connection.CloseAsync();
        await input.Writer.CompleteAsync();
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }

    private sealed class IdleListener : IServerTransportListener
    {
        public System.Net.EndPoint? LocalEndPoint => null;

        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
