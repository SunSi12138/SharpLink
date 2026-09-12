using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;
using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;

namespace SharpLink.UnitTests.Server;

public partial class SharpLinkServerInvocationTests
{
    [Test]
    public async Task FailedInvocationShouldPreserveLeaseCleanupFailure()
    {
        await using var server = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTransport(new IdleListener())
            .Build();
        await using var session = new RpcSession(
            new TestTransportConnection(),
            RpcSessionTestFixture.ServerOptions());
        var lease = new ServiceLease(
            new ThrowingService(),
            new ThrowingScope(),
            disposeService: true);
        var method = typeof(SharpLinkServer).GetMethod(
            "InvokeServiceWithLeaseAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find leased invocation path");

        Exception failure;
        try
        {
            var invocation = (ValueTask)method.Invoke(server,
            [
                new ThrowingStub(),
                lease,
                session,
                new RpcSessionGeneratedServerBridge(session),
                1L,
                1L,
                ReadOnlySequence<byte>.Empty,
                null,
                CancellationToken.None,
                new SharpLinkCallContextSnapshot(session.Id, authentication: null),
                false
            ])!;
            await invocation;
            throw new Exception("expected leased invocation failure");
        }
        catch (Exception exception)
        {
            failure = exception is TargetInvocationException { InnerException: { } inner }
                ? inner
                : exception;
        }

        Ensure(ContainsMessage(failure, "handler failed"),
            "leased invocation must retain the handler failure");
        Ensure(ContainsMessage(failure, "lease cleanup failed"),
            "leased invocation must retain the lease cleanup failure");
    }

    [Test]
    public async Task FullErrorResponseQueueShouldWaitForCapacityWithoutClosingConnection()
    {
        var output = new BlockingFlushPipeWriter();
        await using var harness = new ServerDispatchHarness(
            new SynchronouslyThrowingStub(), output, maxSendQueueBytes: 1);
        harness.Session.SendHealthCheck(99);
        await output.FlushStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var operation = harness.Dispatch(1, ProtocolV2FrameFlags.None);

        Ensure(!operation.IsCompleted,
            "a full response queue must move synchronous error dispatch to the capacity-wait slow path");
        Ensure(harness.Session.IsConnected,
            "response backpressure must not close an otherwise healthy session");
        Ensure(harness.GlobalActiveCalls == 1 && harness.Connection.ActiveCalls == 1,
            "the error response must retain both admission slots while waiting for queue capacity");

        output.ReleaseFlush();
        await operation.AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await harness.Session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(harness.Session.IsConnected,
            "the session must remain usable after deferred error-response admission");
        Ensure(harness.GlobalActiveCalls == 0 && harness.Connection.ActiveCalls == 0,
            "deferred error-response completion must release both call counters");
        EnsureResponseFrame(
            output.WrittenMemory,
            harness.Session.RuntimeContext.Protocol,
            requestId: 1,
            expectedError: SharpLinkErrorCode.Internal,
            expectedPayloadByte: null);
    }

    [Test]
    public async Task FullPayloadResponseQueueShouldWaitForCapacityWithoutClosingConnection()
    {
        var output = new BlockingFlushPipeWriter();
        await using var harness = new ServerDispatchHarness(
            new SynchronouslyRespondingStub(), output, maxSendQueueBytes: 1);
        harness.Session.SendHealthCheck(99);
        await output.FlushStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var operation = harness.Dispatch(2, ProtocolV2FrameFlags.HasReturn);

        Ensure(!operation.IsCompleted,
            "a full response queue must move synchronous payload dispatch to the capacity-wait slow path");
        Ensure(harness.Session.IsConnected,
            "payload-response backpressure must not close an otherwise healthy session");
        Ensure(harness.GlobalActiveCalls == 1 && harness.Connection.ActiveCalls == 1,
            "the payload response must retain both admission slots while waiting for queue capacity");

        output.ReleaseFlush();
        await operation.AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await harness.Session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(harness.Session.IsConnected,
            "the session must remain usable after deferred payload-response admission");
        Ensure(harness.GlobalActiveCalls == 0 && harness.Connection.ActiveCalls == 0,
            "deferred payload-response completion must release both call counters");
        EnsureResponseFrame(
            output.WrittenMemory,
            harness.Session.RuntimeContext.Protocol,
            requestId: 2,
            expectedError: null,
            expectedPayloadByte: SynchronouslyRespondingStub.ResponseByte);
    }

    [Test]
    public async Task AvailableResponseQueueShouldKeepSynchronousDispatchFastPath()
    {
        var output = new Pipe();
        await using var harness = new ServerDispatchHarness(
            new SynchronouslyRespondingStub(), output.Writer, maxSendQueueBytes: 1024);

        var operation = harness.Dispatch(3, ProtocolV2FrameFlags.HasReturn);

        Ensure(operation.IsCompletedSuccessfully,
            "an available response queue must preserve synchronous dispatch completion");
        await operation;
        Ensure(harness.Session.IsConnected, "the synchronous fast path must keep the session healthy");
        Ensure(harness.GlobalActiveCalls == 0 && harness.Connection.ActiveCalls == 0,
            "the synchronous fast path must release both call counters before returning");

        await harness.Session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var read = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        EnsureResponseFrame(
            read.Buffer,
            harness.Session.RuntimeContext.Protocol,
            requestId: 3,
            expectedError: null,
            expectedPayloadByte: SynchronouslyRespondingStub.ResponseByte);
        output.Reader.AdvanceTo(read.Buffer.End);
        await output.Reader.CompleteAsync();
    }
}
