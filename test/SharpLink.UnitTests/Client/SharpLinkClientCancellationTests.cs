using System.Diagnostics;
using System.Threading;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public class SharpLinkClientCancellationTests
{
    [Test]
    public async Task InvokeWithDefaultTimeoutNoPayloadAsyncShouldTimeoutAndSendCancel()
    {
        var clock = new ManualTimeProvider();
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseTimeProvider(clock).UseRequestTimeout(TimeSpan.FromMilliseconds(80)));

        await client.ConnectAsync();
        var prepared = await ClientInvokerTestHelper.PrepareAsync(client, transport);

        var invokeTask = ClientInvokerTestHelper.InvokeUnaryAsync(client, prepared: prepared).AsTask();
        var callPacket = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request).WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromMilliseconds(80));
        var exception = await EnsureThrows<SharpLinkException>(invokeTask);
        Ensure(exception.Code == SharpLinkErrorCode.DeadlineExceeded, "timeout should map to DeadlineExceeded");

        var cancelPacket = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Cancel).WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(cancelPacket.RequestId == callPacket.RequestId, "cancel should target same request");
    }

    [Test]
    public async Task InvokeCancellableNoPayloadAsyncShouldUseOperationCanceledWhenUserTokenCancels()
    {
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.CancellationReason);
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseRequestTimeout(TimeSpan.FromSeconds(5)));

        await client.ConnectAsync();
        var prepared = await ClientInvokerTestHelper.PrepareAsync(client, transport);

        using var cts = new CancellationTokenSource();
        var invokeTask = ClientInvokerTestHelper.InvokeUnaryAsync(
            client,
            cancellationToken: cts.Token, prepared: prepared).AsTask();

        var request = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request).WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await EnsureThrows<OperationCanceledException>(invokeTask);
        var cancel = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Cancel).WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(cancel.Header.RequestId == request.RequestId, "user cancel request ID");
        Ensure(cancel.Payload is [(byte)ProtocolV2CancelReason.UserCancellation],
            "user cancellation should send its negotiated reason");
    }

    [Test]
    public async Task ReceiveCancelPacketShouldNotBreakPendingRequest()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseRequestTimeout(TimeSpan.FromSeconds(2)));

        await client.ConnectAsync();
        var prepared = await ClientInvokerTestHelper.PrepareAsync(client, transport);

        var invokeTask = ClientInvokerTestHelper.InvokeUnaryAsync(client, prepared: prepared).AsTask();
        var callPacket = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request).WaitAsync(TimeSpan.FromSeconds(5));

        await transport.Connection.InjectPacketAsync(
            ProtocolV2FrameType.Cancel, ProtocolV2FrameFlags.None, unchecked((long)callPacket.RequestId));
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)callPacket.RequestId));

        var value = await invokeTask;
        Ensure(value == 0, "zero-valued Int32 response");
    }

    [Test]
    public async Task InvokeOneWayNoPayloadShouldNotCreateTimeoutCancel()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseRequestTimeout(TimeSpan.FromMilliseconds(80)));

        await client.ConnectAsync();
        var prepared = await ClientInvokerTestHelper.PrepareAsync(client, transport);

        await ClientInvokerTestHelper.InvokeOneWayAsync(client, prepared);
        var sent = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request).WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(sent.Flags.HasFlag(ProtocolV2FrameFlags.OneWay) && !sent.Flags.HasFlag(ProtocolV2FrameFlags.Cancellable),
            "untimed plain OneWay must emit a real non-cancellable head");
        var hasCancel = await transport.Connection.TryWaitForSentPacket(
            ProtocolV2FrameType.Cancel, TimeSpan.FromMilliseconds(200));
        Ensure(!hasCancel, "oneway call should not send timeout cancel");
    }

    [Test]
    // ActivityListener registration is process-wide, so this assertion must own that listener window.
    [NotInParallel]
    public async Task EarlyServerStreamDisposalShouldSendConsumerAbandonedReason()
    {
        using var telemetryListener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "SharpLink.Client",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.PropagationData,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                ActivitySamplingResult.PropagationData
        };
        ActivitySource.AddActivityListener(telemetryListener);

        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.CancellationReason);
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseRequestTimeout(TimeSpan.FromSeconds(5)));

        await client.ConnectAsync();
        var prepared = await ClientInvokerTestHelper.PrepareAsync(client, transport);
        var stream = ClientInvokerTestHelper.InvokeServerStreaming(client, prepared: prepared);
        ProtocolV2FrameHeader request;
        await using (var enumerator = stream.GetAsyncEnumerator())
        {
            var reading = enumerator.MoveNextAsync().AsTask();
            request = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await transport.Connection.InjectInt32ServerStreamItemAsync(request.RequestId, 42);
            Ensure(await reading.WaitAsync(TimeSpan.FromSeconds(5)) && enumerator.Current == 42,
                "prepared enumeration must read at least one item before the consumer leaves");
        }

        var cancel = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Cancel).WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(cancel.Header.RequestId == request.RequestId, "consumer abandonment request ID");
        Ensure(cancel.Payload is [(byte)ProtocolV2CancelReason.ConsumerAbandoned],
            "early stream disposal should send ConsumerAbandoned");
    }

    private static async Task<TException> EnsureThrows<TException>(Task task) where TException : Exception
    {
        try
        {
            await task;
            throw new Exception($"expected {typeof(TException).Name}");
        }
        catch (TException exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

}
