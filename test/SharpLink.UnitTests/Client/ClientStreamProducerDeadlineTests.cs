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
