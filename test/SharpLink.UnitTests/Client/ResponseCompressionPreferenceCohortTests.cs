using System.IO.Pipelines;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public class ResponseCompressionPreferenceCohortTests
{
    [Test]
    public async Task FailedSessionShouldNotPreventLaterCohortSessionFromReconciling()
    {
        using var failedContext = CreateContext(maxSendQueueBytes: 1);
        using var healthyContext = CreateContext(maxSendQueueBytes: 1024);
        var failedInput = new Pipe();
        var failedOutput = new Pipe();
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
