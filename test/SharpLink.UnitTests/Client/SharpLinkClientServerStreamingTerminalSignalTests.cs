using System.Reflection;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientServerStreamingTerminalSignalTests
{
    [Test]
    public async Task TimedServerStreamingShouldLeaveNoFrameworkTaskWhenDeadlineWinsAStalledWrite()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.CancellationReason);
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseTimeProvider(timeProvider));
        await client.ConnectAsync();

        var method = new RpcMethodDescriptor(
            ContractId: 1,
            MethodId: 313,
            Kind: RpcMethodKind.ServerStreaming,
            HasResponsePayload: true,
            HasClientStreams: false,
            HasMethodTimeout: true,
            MethodTimeout: TimeSpan.FromSeconds(5));
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var connection = GetOnlyReadyConnection(client);
        await connection.Session.FlushSendQueueAsync();
        await DrainSentFramesAsync(transport);

        using var stall = new ManualResetEventSlim(initialState: false);
        var writeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Connection.RunOnNextOutputBufferRequest(() =>
        {
            writeStarted.TrySetResult();
            stall.Wait(TimeSpan.FromSeconds(30));
        });

        try
        {
            await using var enumerator = channel.InvokeServerStreamingAsync(
                method,
                in request,
                RpcEmptyRequestCodec.Instance,
                channel.RuntimeContext.Codecs.GetCodec<int>(),
                metadata: null,
                cancellationToken: default).GetAsyncEnumerator();
            var moveNext = enumerator.MoveNextAsync().AsTask();

            await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(!moveNext.IsCompleted,
                "the response stream must still be waiting while the accepted Request is stalled");

            timeProvider.Advance(TimeSpan.FromSeconds(5));
            var failure = await CaptureSharpLinkExceptionAsync(moveNext).WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
                "the pending deadline must remain the user-visible terminal result");
            Ensure(await WaitForFrameworkTaskToFinishAsync(client, "ServerStreamingInvoker"),
                "the server-streaming start task must leave the supervisor before the transport recovers");

            stall.Set();
            var published = await transport.Connection.TryReadNextSentFrameAsync(TimeSpan.FromSeconds(5));
            var cancelled = await transport.Connection.TryReadNextSentFrameAsync(TimeSpan.FromSeconds(5));
            Ensure(published?.Header.Type == ProtocolV2FrameType.Request,
                "the transport must retain the Request accepted before the deadline won");
            Ensure(cancelled?.Header.Type == ProtocolV2FrameType.Cancel &&
                   cancelled!.Value.Header.RequestId == published!.Value.Header.RequestId,
                "the terminal deadline cancel must stay ordered behind its accepted Request");
        }
        finally
        {
            stall.Set();
        }
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

    private static async Task DrainSentFramesAsync(TestClientTransportFactory transport)
    {
        while (await transport.Connection.TryReadNextSentFrameAsync(TimeSpan.FromMilliseconds(20)) is not null)
        {
        }
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

    private static async Task<bool> WaitForFrameworkTaskToFinishAsync(
        SharpLinkClient client,
        string operation)
    {
        var supervisor = (FrameworkTaskSupervisor)(typeof(SharpLinkClient).GetField(
                "_frameworkTasks",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(client) ?? throw new Exception("cannot find the framework task supervisor"));
        for (var attempt = 0; attempt < 250; attempt++)
        {
            var active = false;
            foreach (var entry in supervisor.CaptureSnapshot().Operations)
            {
                if (entry.Operation == operation)
                {
                    active = true;
                    break;
                }
            }

            if (!active)
                return true;
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        return false;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
