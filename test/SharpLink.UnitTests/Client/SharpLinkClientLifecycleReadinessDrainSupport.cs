using System.Buffers.Binary;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;
using static SharpLink.UnitTests.Client.SharpLinkClientLifecycleSharedSupport;

namespace SharpLink.UnitTests.Client;

internal static class SharpLinkClientLifecycleReadinessDrainSupport
{
    internal static SharpLinkRuntimeContext CreateRuntimeContext()
        => new SharpLinkRuntimeContextBuilder()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .Build();

    internal static RpcSession CreateReadySession(SharpLinkRuntimeContext context)
    {
        var session = new RpcSession(
            new TestTransportConnection(),
            RpcSessionTestFixture.ClientOptions(context));
        RpcSessionTestFixture.CompleteHandshake(session);
        return session;
    }

    internal static async Task ObserveFailureAsync(ValueTask<int> operation)
    {
        try
        {
            await operation;
        }
        catch (InvalidOperationException)
        {
        }
    }

    internal static async Task InjectGoAwayAsync(TestTransportConnection connection)
    {
        var payload = new PooledByteBufferWriter();
        var lastAccepted = payload.GetSpan(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(lastAccepted, 0);
        payload.Advance(sizeof(ulong));
        ProtocolV2PayloadCodec.WriteError(
            payload,
            SharpLinkErrorCode.Unavailable,
            "rolling restart",
            1024,
            out _);

        await connection.InjectFrameAsync(
            ProtocolV2FrameType.GoAway,
            ProtocolV2FrameFlags.Error,
            0,
            payload.WrittenMemory);
    }

    internal sealed class AdmitFirstRejectSecondPolicy(TimeSpan retryAfter) : ISharpLinkEndpointAdmissionPolicy
    {
        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint,
            in RpcMethodDescriptor method)
            => endpoint.Endpoint.Id == "first"
                ? new SharpLinkEndpointAdmissionDecision(true, Token: 1, RetryAfter: null)
                : new SharpLinkEndpointAdmissionDecision(false, Token: 0, RetryAfter: retryAfter);

        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
        }
    }

    internal sealed class BlockingDisposeConnection : ITransportConnection
    {
        private readonly System.IO.Pipelines.Pipe _input = new();
        private readonly System.IO.Pipelines.Pipe _output = new();
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id { get; } = "blocking-dispose";
        public System.IO.Pipelines.PipeReader Input => _input.Reader;
        public System.IO.Pipelines.PipeWriter Output => _output.Writer;
        public System.Net.EndPoint? LocalEndPoint => null;
        public System.Net.EndPoint? RemoteEndPoint => null;

        public ValueTask DisposeAsync()
        {
            DisposeStarted.TrySetResult();
            return new ValueTask(_release.Task);
        }

        internal void ReleaseDispose() => _release.TrySetResult();
    }
}
