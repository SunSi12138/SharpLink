using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public class ClientStreamProducerDeadlineTests
{
    [Test]
    public async Task ExpiredCallShouldNotReenterProducerBeforeDeadlineTimerRuns()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseTimeProvider(timeProvider));
        await client.ConnectAsync();

        var connection = GetOnlyReadyConnection(client);
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), timeProvider);
        var codec = client.RuntimeContext.Codecs.GetCodec<int>();
        var operation = connection.PendingCalls.Rent(
            codec,
            PendingCallKind.ClientStreaming,
            deadline,
            CancellationToken.None,
            out var requestId,
            hasResponsePayload: true,
            responseNullable: false);
        var producerToken = connection.PendingCalls.GetProducerCancellationToken(requestId);
        var producer = new MoveNextProbeStream();

        // Cross the monotonic boundary without running the pending-call deadline timer. The
        // producer-side re-entry claimant, not timer scheduling, must stop the next MoveNextAsync.
        timeProvider.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(1));
        var sendFailure = await CaptureSharpLinkExceptionAsync(
            connection.SendClientStreamAsync(
                requestId,
                0,
                producer,
                codec,
                producerToken));

        Ensure(sendFailure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "producer re-entry after the frozen deadline must fail as DeadlineExceeded");
        Ensure(producer.MoveNextCalls == 0,
            "an expired call must not invoke user MoveNextAsync before observing its terminal owner");

        var operationFailure = await CaptureSharpLinkExceptionAsync(
            operation.AsValueTask().AsTask());
        Ensure(operationFailure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "the owning pending operation must publish the same deadline terminal");
    }

    [Test]
    public async Task StreamCodecCapabilityShouldBeReadOncePerClientStream()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);
        await client.ConnectAsync();

        var connection = GetOnlyReadyConnection(client);
        var responseCodec = client.RuntimeContext.Codecs.GetCodec<int>();
        _ = connection.PendingCalls.Rent(
            responseCodec,
            PendingCallKind.ClientStreaming,
            default,
            CancellationToken.None,
            out var requestId,
            hasResponsePayload: true,
            responseNullable: false);
        var producerToken = connection.PendingCalls.GetProducerCancellationToken(requestId);
        var codec = new CountingNonExactSizedIntCodec();

        await connection.SendClientStreamAsync(
            requestId,
            0,
            Values(1, 2, 3),
            codec,
            producerToken);

        Ensure(codec.CanExactSizeReadCount == 1,
            "the client stream must read CanExactSize once for the whole stream");
        Ensure(codec.TryGetEncodedSizeCount == 0,
            "a codec that cannot exact-size must never enter per-item sizing");
        Ensure(codec.SerializeCount == 3,
            "the unsized client stream must still serialize every item exactly once");
    }

    private static ClientConnection GetOnlyReadyConnection(SharpLinkClient client)
    {
        var connections = (ClientConnection[])(typeof(SharpLinkClient).GetField(
                "_readyConnections",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(client) ?? throw new Exception("cannot find ready connection selection snapshot"));
        Ensure(connections.Length == 1, "expected exactly one ready connection");
        return connections[0];
    }

    private static async Task<SharpLinkException> CaptureSharpLinkExceptionAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
        throw new Exception("expected SharpLinkException");
    }

    private static async Task<SharpLinkException> CaptureSharpLinkExceptionAsync<T>(Task<T> operation)
    {
        try
        {
            _ = await operation;
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
        throw new Exception("expected SharpLinkException");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static async IAsyncEnumerable<int> Values(params int[] values)
    {
        foreach (var value in values)
        {
            yield return value;
            await Task.Yield();
        }
    }

    private sealed class CountingNonExactSizedIntCodec : IRpcCodec<int>, IRpcSizedCodec<int>
    {
        private int _canExactSizeReadCount;
        private int _tryGetEncodedSizeCount;
        private int _serializeCount;

        internal int CanExactSizeReadCount => Volatile.Read(ref _canExactSizeReadCount);
        internal int TryGetEncodedSizeCount => Volatile.Read(ref _tryGetEncodedSizeCount);
        internal int SerializeCount => Volatile.Read(ref _serializeCount);

        public bool CanExactSize
        {
            get
            {
                Interlocked.Increment(ref _canExactSizeReadCount);
                return false;
            }
        }

        public void Serialize(in int value, IBufferWriter<byte> buffer)
        {
            Interlocked.Increment(ref _serializeCount);
            var span = buffer.GetSpan(sizeof(int));
            BitConverter.TryWriteBytes(span, value);
            buffer.Advance(sizeof(int));
        }

        public int Deserialize(in ReadOnlySequence<byte> buffer)
            => BitConverter.ToInt32(buffer.FirstSpan);

        public bool TryGetEncodedSize(in int value, out int size)
        {
            Interlocked.Increment(ref _tryGetEncodedSizeCount);
            size = sizeof(int);
            return true;
        }

        public bool TryGetEncodedSize(
            in int value,
            out int size,
            out IRpcSizedCodecSnapshot? snapshot)
        {
            Interlocked.Increment(ref _tryGetEncodedSizeCount);
            size = sizeof(int);
            snapshot = null;
            return true;
        }

        public void SerializeSized(
            in int value,
            IBufferWriter<byte> buffer,
            int size,
            IRpcSizedCodecSnapshot? snapshot)
            => throw new InvalidOperationException("non-exact codec must not use SerializeSized");

        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)
            => throw new InvalidOperationException("non-exact codec must not release a snapshot");
    }

    private sealed class MoveNextProbeStream : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal int MoveNextCalls;

        public int Current => 7;

        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => this;

        public ValueTask<bool> MoveNextAsync()
        {
            MoveNextCalls++;
            return ValueTask.FromResult(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
