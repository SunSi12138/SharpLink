using System.Buffers;
using System.IO.Pipelines;
using System.Linq;

namespace SharpLink.UnitTests.Runtime;

public class CompressionRuntimePolicyTests
{
    [Test]
    public void InvalidLocalPolicyShouldNotPublishPartialState()
    {
        var options = new SharpLinkCompressionSendPolicy
        {
            MinimumPayloadBytes = 1024,
            MinimumSavingsBytes = 64,
            MinimumSavingsRatio = 0.05
        };
        var state = CompressionSendPolicyState.CreateInitial(options);
        var before = state.Current;
        var failed = false;
        try
        {
            state.Update(new SharpLinkCompressionSendPolicy
            {
                Enabled = false,
                MinimumPayloadBytes = 1,
                MinimumSavingsBytes = 1,
                MinimumSavingsRatio = double.NaN
            });
        }
        catch (ArgumentOutOfRangeException)
        {
            failed = true;
        }

        Ensure(failed, "invalid ratio rejected");
        Ensure(ReferenceEquals(before, state.Current), "invalid candidate must not publish");
    }

    [Test]
    public async Task LocalPolicyAndRemotePreferenceShouldApplyAtNextFrameDecision()
    {
        var provider = new TestCompressionProvider();
        using var context = new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.Compression.Providers.Add(provider))
            .Build();
        var policy = CompressionSendPolicyState.CreateInitial(new SharpLinkCompressionSendPolicy());
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "dynamic-compression-policy",
            input.Reader,
            output.Writer,
            new RpcSessionCreationOptions(RpcSessionRole.Server, context, null, policy),
            completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(
            session,
            ProtocolV2Capabilities.Compression,
            compressionBinding: context.Compression.ProviderBindings[0]);
        session.InitializeServerResponseCompressionPreference(5, allowResponseCompression: true);

        var payload = Enumerable.Repeat((byte)0x4c, 4096).ToArray();
        var compressed = await SendResponseAndReadHeaderAsync(session, output.Reader, 1, payload);
        Ensure((compressed.Flags & ProtocolV2FrameFlags.Compressed) != 0, "initial response should compress");

        policy.Update(new SharpLinkCompressionSendPolicy
        {
            Enabled = false,
            MinimumPayloadBytes = 1024,
            MinimumSavingsBytes = 64,
            MinimumSavingsRatio = 0.05
        });
        var locallyDisabled = await SendResponseAndReadHeaderAsync(session, output.Reader, 2, payload);
        Ensure((locallyDisabled.Flags & ProtocolV2FrameFlags.Compressed) == 0, "disabled local response policy should be raw");

        policy.Update(new SharpLinkCompressionSendPolicy
        {
            Enabled = true,
            MinimumPayloadBytes = 1024,
            MinimumSavingsBytes = 64,
            MinimumSavingsRatio = 0.05
        });
        _ = session.ApplyServerResponseCompressionPreferenceUpdate(
            new ProtocolV2ResponseCompressionPreferenceUpdate(6, false));
        var remotelyDisabled = await SendResponseAndReadHeaderAsync(session, output.Reader, 3, payload);
        Ensure((remotelyDisabled.Flags & ProtocolV2FrameFlags.Compressed) == 0, "disabled client response preference should be raw");

        var applied = session.ApplyServerResponseCompressionPreferenceUpdate(
            new ProtocolV2ResponseCompressionPreferenceUpdate(7, true));
        Ensure(applied == 7, "new remote preference generation applied");
        var enabledAgain = await SendResponseAndReadHeaderAsync(session, output.Reader, 4, payload);
        Ensure((enabledAgain.Flags & ProtocolV2FrameFlags.Compressed) != 0, "re-enabled response preference should compress");

        var stale = session.ApplyServerResponseCompressionPreferenceUpdate(
            new ProtocolV2ResponseCompressionPreferenceUpdate(6, false));
        Ensure(stale == 7, "stale generation must ACK current generation");
        var afterStale = await SendResponseAndReadHeaderAsync(session, output.Reader, 5, payload);
        Ensure((afterStale.Flags & ProtocolV2FrameFlags.Compressed) != 0, "stale update must not roll back state");

        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    [Test]
    public async Task ClientControlShouldCoalesceToLatestGenerationAndUseCumulativeAck()
    {
        var provider = new TestCompressionProvider();
        using var context = new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.Compression.Providers.Add(provider))
            .Build();
        var policy = CompressionSendPolicyState.CreateInitial(new SharpLinkCompressionSendPolicy());
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "compression-preference-coalesce",
            input.Reader,
            output.Writer,
            new RpcSessionCreationOptions(RpcSessionRole.Client, context, null, policy),
            completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(
            session,
            ProtocolV2Capabilities.Compression,
            compressionBinding: context.Compression.ProviderBindings[0]);
        session.InitializeClientResponseCompressionPreference(ResponseCompressionPreferenceSnapshot.InitialAllowed);

        session.ReconcileResponseCompressionPreference(new ResponseCompressionPreferenceSnapshot(1, false));
        session.ReconcileResponseCompressionPreference(new ResponseCompressionPreferenceSnapshot(2, true));
        session.ReconcileResponseCompressionPreference(new ResponseCompressionPreferenceSnapshot(3, false));
        await session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        var first = await ReadPreferenceUpdateAsync(output.Reader, context.Protocol);
        Ensure(first.Generation == 1 && !first.AllowResponseCompression, "first generation in flight");

        session.ApplyResponseCompressionPreferenceAck(1);
        await session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var latest = await ReadPreferenceUpdateAsync(output.Reader, context.Protocol);
        Ensure(latest.Generation == 3 && !latest.AllowResponseCompression, "intermediate generation should be coalesced");

        var waiter = session.WaitForResponseCompressionPreferenceAsync(2, CancellationToken.None).AsTask();
        Ensure(!waiter.IsCompleted, "generation 2 waiter should await cumulative progress");
        session.ApplyResponseCompressionPreferenceAck(3);
        await waiter.WaitAsync(TimeSpan.FromSeconds(2));
        await session.WaitForResponseCompressionPreferenceAsync(3, CancellationToken.None);

        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    [Test]
    public async Task SessionWithoutNegotiatedCompressionShouldNotEmitPreferenceUpdate()
    {
        var provider = new TestCompressionProvider();
        using var context = new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.Compression.Providers.Add(provider))
            .Build();
        var policy = CompressionSendPolicyState.CreateInitial(new SharpLinkCompressionSendPolicy());
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "compression-no-common-profile",
            input.Reader,
            output.Writer,
            new RpcSessionCreationOptions(RpcSessionRole.Client, context, null, policy),
            completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(session);
        session.InitializeClientResponseCompressionPreference(ResponseCompressionPreferenceSnapshot.InitialAllowed);

        session.ReconcileResponseCompressionPreference(new ResponseCompressionPreferenceSnapshot(1, false));
        await session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        if (output.Reader.TryRead(out var read))
        {
            try
            {
                Ensure(read.Buffer.IsEmpty, "no-common-profile session must not emit preference control frame");
            }
            finally
            {
                output.Reader.AdvanceTo(read.Buffer.End);
            }
        }

        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    private static async Task<ProtocolV2FrameHeader> SendResponseAndReadHeaderAsync(
        RpcSession session,
        PipeReader reader,
        ulong requestId,
        byte[] payload)
    {
        var writer = session.RentFrameWriter();
        using (writer.BeginPacketScope(
                   ProtocolV2FrameType.Response,
                   ProtocolV2FrameFlags.None,
                   requestId))
        {
            writer.Write(payload);
        }
        session.SendPacket(writer);
        await session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        var read = await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var buffer = read.Buffer;
        Ensure(ProtocolV2FrameParser.TryReadFrame(
            ref buffer,
            session.RuntimeContext.Protocol,
            out var header,
            out _), "response frame parse");
        reader.AdvanceTo(buffer.Start, buffer.End);
        return header;
    }

    private static async Task<ProtocolV2ResponseCompressionPreferenceUpdate> ReadPreferenceUpdateAsync(
        PipeReader reader,
        SharpLinkProtocolOptions limits)
    {
        var read = await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var buffer = read.Buffer;
        Ensure(ProtocolV2FrameParser.TryReadFrame(ref buffer, limits, out var header, out var payload), "preference update parse");
        Ensure(header.Type == ProtocolV2FrameType.ResponseCompressionPreferenceUpdate, "preference update frame type");
        var update = ProtocolV2PayloadCodec.ReadResponseCompressionPreferenceUpdate(payload);
        reader.AdvanceTo(buffer.Start, buffer.End);
        return update;
    }

    private static void Ensure(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException($"Assertion failed: {description}.");
    }
}
