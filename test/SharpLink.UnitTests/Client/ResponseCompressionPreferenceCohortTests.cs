using System.Diagnostics;
using System.IO.Pipelines;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public class ResponseCompressionPreferenceCohortTests
{
    [Test]
    public async Task FailedSessionShouldNotPreventLaterCohortSessionFromReconciling()
    {
        using var failedContext = CreateContext(maxSendQueueBytes: 32);
        using var healthyContext = CreateContext(maxSendQueueBytes: 1024);
        var failedInput = new Pipe();
        var failedOutput = CreateBackpressuredPipe();
        var healthyInput = new Pipe();
        var healthyOutput = new Pipe();
        var policy = CompressionSendPolicyState.CreateInitial(new SharpLinkCompressionSendPolicy());

        await using var failedSession = CreateClientSession(
            "compression-cohort-failed",
            failedContext,
            failedInput,
            failedOutput,
            policy);
        await using var healthySession = CreateClientSession(
            "compression-cohort-healthy",
            healthyContext,
            healthyInput,
            healthyOutput,
            policy);

        failedSession.SendPacket(CreateBlockingFrame(failedSession));
        await WaitUntilAsync(
            () => failedSession.QueuedSendBytes > 0,
            TimeSpan.FromSeconds(2));
        Ensure(failedSession.QueuedSendBytes > 0,
            "the failed cohort session must retain a backpressured frame before preference propagation");

        var desired = new ResponseCompressionPreferenceSnapshot(1, false);
        var convergence = SharpLinkClient.ApplyResponseCompressionPreferenceToCohortAsync(
            [failedSession, healthySession],
            desired,
            CancellationToken.None).AsTask();

        var update = await ReadPreferenceUpdateAsync(healthyOutput.Reader, healthyContext.Protocol);
        Ensure(update.Generation == desired.Generation && !update.AllowResponseCompression,
            "later healthy cohort session must receive the desired update after an earlier send failure");
        healthySession.ApplyResponseCompressionPreferenceAck(update.Generation);

        var failure = await CaptureExceptionAsync(convergence.WaitAsync(TimeSpan.FromSeconds(2)));
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted },
            "cohort convergence should report the first session send failure after later sessions are attempted");

        await failedOutput.Reader.CompleteAsync();
        await failedInput.Writer.CompleteAsync();
        await healthyOutput.Reader.CompleteAsync();
        await healthyInput.Writer.CompleteAsync();
    }

    [Test]
    public async Task CallerCancellationShouldNotBecomeAggregateFailureForMultiSessionCohort()
    {
        using var firstContext = CreateContext(maxSendQueueBytes: 1024);
        using var secondContext = CreateContext(maxSendQueueBytes: 1024);
        var firstInput = new Pipe();
        var firstOutput = new Pipe();
        var secondInput = new Pipe();
        var secondOutput = new Pipe();
        var policy = CompressionSendPolicyState.CreateInitial(new SharpLinkCompressionSendPolicy());

        await using var firstSession = CreateClientSession(
            "compression-cohort-cancel-first",
            firstContext,
            firstInput,
            firstOutput,
            policy);
        await using var secondSession = CreateClientSession(
            "compression-cohort-cancel-second",
            secondContext,
            secondInput,
            secondOutput,
            policy);
        using var cancellation = new CancellationTokenSource();

        var desired = new ResponseCompressionPreferenceSnapshot(1, false);
        var convergence = SharpLinkClient.ApplyResponseCompressionPreferenceToCohortAsync(
            [firstSession, secondSession],
            desired,
            cancellation.Token).AsTask();

        var firstUpdate = await ReadPreferenceUpdateAsync(firstOutput.Reader, firstContext.Protocol);
        var secondUpdate = await ReadPreferenceUpdateAsync(secondOutput.Reader, secondContext.Protocol);
        Ensure(firstUpdate.Generation == desired.Generation && !firstUpdate.AllowResponseCompression,
            "first cohort session must receive the desired update before caller cancellation");
        Ensure(secondUpdate.Generation == desired.Generation && !secondUpdate.AllowResponseCompression,
            "second cohort session must receive the desired update before caller cancellation");

        cancellation.Cancel();
        var failure = await CaptureExceptionAsync(convergence.WaitAsync(TimeSpan.FromSeconds(2)));
        Ensure(failure is OperationCanceledException,
            "caller cancellation must remain a cancellation for a multi-session cohort");
        Ensure(failure is not AggregateException,
            "caller cancellation must not be aggregated as a session convergence failure");

        await firstOutput.Reader.CompleteAsync();
        await firstInput.Writer.CompleteAsync();
        await secondOutput.Reader.CompleteAsync();
        await secondInput.Writer.CompleteAsync();
    }

    private static SharpLinkRuntimeContext CreateContext(int maxSendQueueBytes)
        => new SharpLinkRuntimeContextBuilder()
            .Configure(options =>
            {
                options.FlowControl.MaxSendQueueBytes = maxSendQueueBytes;
                options.Compression.Providers.Add(new ControlOnlyCompressionProvider());
            })
            .Build();

    private static RpcSession CreateClientSession(
        string name,
        SharpLinkRuntimeContext context,
        Pipe input,
        Pipe output,
        CompressionSendPolicyState policy)
    {
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            name,
            input.Reader,
            output.Writer,
            new RpcSessionCreationOptions(RpcSessionRole.Client, context, null, policy),
            completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(
            session,
            ProtocolV2Capabilities.Compression,
            compressionBinding: context.Compression.ProviderBindings[0]);
        session.InitializeClientResponseCompressionPreference(ResponseCompressionPreferenceSnapshot.InitialAllowed);
        return session;
    }

    private static Pipe CreateBackpressuredPipe()
        => new(new PipeOptions(pauseWriterThreshold: 1, resumeWriterThreshold: 0));

    private static IRpcByteBufferWriter CreateBlockingFrame(RpcSession session)
    {
        var writer = session.RuntimeContext.Buffers.Rent();
        using (writer.BeginPacketScope(
                   ProtocolV2FrameType.Response,
                   ProtocolV2FrameFlags.None,
                   requestId: 77))
        {
            writer.Write(new byte[64]);
        }
        return writer;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!condition() && Stopwatch.GetTimestamp() < deadline)
            await Task.Delay(10);
    }

    private static async Task<ProtocolV2ResponseCompressionPreferenceUpdate> ReadPreferenceUpdateAsync(
        PipeReader reader,
        SharpLinkProtocolOptions limits)
    {
        var read = await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var buffer = read.Buffer;
        Ensure(ProtocolV2FrameParser.TryReadFrame(ref buffer, limits, out var header, out var payload),
            "preference update frame should parse");
        Ensure(header.Type == ProtocolV2FrameType.ResponseCompressionPreferenceUpdate,
            "healthy cohort session should emit a response-compression preference update");
        var update = ProtocolV2PayloadCodec.ReadResponseCompressionPreferenceUpdate(payload);
        reader.AdvanceTo(buffer.Start, buffer.End);
        return update;
    }

    private static async Task<Exception?> CaptureExceptionAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException($"Assertion failed: {description}.");
    }

    private sealed class ControlOnlyCompressionProvider : ISharpLinkCompressionProvider
    {
        public string WireProfile => "test.control-only/v1";

        public bool TryCompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => false;

        public void Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Control-only provider should not decode data frames.");
    }
}
