using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Reflection;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public partial class SharpLinkServerInvocationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task CompressedTerminalStreamShouldRefundOriginalBytesAtActualRequestLoopBoundary(bool terminalDuringDecode)
        => RunCompressedTerminalLoopAsync(terminalDuringDecode, existingRoute: true, originalLength: 37);

    [Test]
    public Task CompressedTerminalStreamDecodeFailureShouldRefundValidatedOriginalBytesOnce()
        => RunCompressedTerminalLoopAsync(terminalDuringDecode: true, existingRoute: true,
            originalLength: 37, failDecode: true);

    [Test]
    public Task CompressedTerminalStreamWithoutRouteShouldNotDecodeOrCreateCredit()
        => RunCompressedTerminalLoopAsync(terminalDuringDecode: false, existingRoute: false, originalLength: 37);

    [Test]
    [Arguments(0u)]
    [Arguments(uint.MaxValue)]
    public Task CompressedTerminalStreamShouldValidateOriginalLengthBeforeDiscard(uint originalLength)
        => RunCompressedTerminalLoopAsync(terminalDuringDecode: false, existingRoute: true, originalLength);

    private static async Task RunCompressedTerminalLoopAsync(bool terminalDuringDecode, bool existingRoute,
        uint originalLength, bool failDecode = false)
    {
        const long requestId = 9017;
        var provider = new TerminalLoopCompressionProvider { FailDecode = failDecode };
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseRuntime(options => options.Compression.Providers.Add(provider))
            .UseTransport(new IdleListener()).Build();
        var runtime = (SharpLinkRuntimeContext)typeof(SharpLinkServer).GetField(
            "_runtimeContext", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "compressed-terminal-loop", input.Reader, output.Writer,
            RpcSessionTestFixture.ServerOptions(runtime), completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(session,
            ProtocolV2Capabilities.Compression | ProtocolV2Capabilities.FlowControl,
            streamReceiveWindowBytes: 64, connectionReceiveWindowBytes: 128,
            compressionBinding: new SharpLinkCompressionProviderBinding(provider.WireProfile, provider));
        var calls = CreateCallCancellations(runtime);
        var connection = new ServerConnectionState(session, new ServerGeneratedBridge(server, session, calls),
            calls, CancellationToken.None, runtime.TimeProvider);
        Ensure(connection.MarkReady(null), "compressed terminal test connection must be ready");
        var state = ServerCallCancellationState.Rent(requestId, default, runtime.TimeProvider,
            CancellationToken.None, CancellationToken.None, supportsCooperativeCancellation: true);
        calls.Set(requestId, state);
        var child = new TerminalLoopRecordingDispatcher();
        if (existingRoute)
        {
            session.StreamManager.ReservePreAdmissionStreams(requestId, 1, runtime.Buffers,
                _ => true, _ => { }, () => throw new InvalidOperationException("unexpected stream capacity"),
                retainUntilLocalCompletion: true);
            session.StreamManager.Register(requestId, 1, child);
        }
        if (terminalDuringDecode)
        {
            provider.BeforeOutput = () =>
            {
                session.StreamManager.AbandonExistingRequestStreams(requestId, 1);
                Ensure(state.TryClaimResponse(),
                    "the call must become terminal while the provider owns its decoded buffer");
            };
        }
        else
        {
            session.StreamManager.AbandonExistingRequestStreams(requestId, 1);
            Ensure(state.TryClaimResponse(), "the call must be terminal before compressed input arrives");
        }

        try
        {
            var loopMethod = typeof(SharpLinkServer).GetMethod("ProcessRequestLoop",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var loop = (Task)loopMethod.Invoke(server, [connection])!;
            using var frames = BuildTerminalCompressedFrames(requestId, originalLength);
            await input.Writer.WriteAsync(frames.WrittenMemory);
            await input.Writer.CompleteAsync();
            var malformed = originalLength is 0 or uint.MaxValue;
            if (malformed)
                await Assert.That(async () => await loop.WaitAsync(TimeSpan.FromSeconds(5)))
                    .Throws<SharpLinkProtocolViolationException>();
            else
                await loop.WaitAsync(TimeSpan.FromSeconds(5));

            await session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            var (updates, credit) = ReadTerminalWindowUpdates(output.Reader, runtime.Protocol, requestId);
            var shouldRefund = existingRoute && !malformed;
            await Assert.That(updates).IsEqualTo(shouldRefund ? 1 : 0);
            await Assert.That(credit).IsEqualTo(shouldRefund ? 37L : 0L);
            await Assert.That(provider.DecompressCount).IsEqualTo(terminalDuringDecode ? 1 : 0);
            await Assert.That(child.DispatchCount).IsEqualTo(0);
            if (!malformed)
                await Assert.That(session.StreamManager.ActiveStreamCount).IsEqualTo(0);
            if (terminalDuringDecode)
            {
                var owner = provider.DecodedOwner ?? throw new InvalidOperationException("provider did not receive an owned writer");
                // The loop has completed and its send queue is flushed; no writer lease remains.
                await Assert.That(() => owner.WrittenCount).Throws<ObjectDisposedException>();
            }
        }
        finally
        {
            // Remove the pooled state before returning it; connection teardown must never dispose
            // a reference that another request could already have rented from the pool.
            calls.TryRemove(requestId, state);
            state.Dispose();
            session.StreamManager.CompleteRequestStreams(requestId, null);
            await connection.CloseAsync();
        }
    }

    private static PooledByteBufferWriter BuildTerminalCompressedFrames(long requestId, uint originalLength)
    {
        var frames = new PooledByteBufferWriter();
        var data = ProtocolV2FrameWriter.BeginFrame(frames, ProtocolV2FrameType.StreamData,
            ProtocolV2FrameFlags.Compressed, unchecked((ulong)requestId));
        Span<byte> envelope = stackalloc byte[sizeof(ushort) + sizeof(uint) + 1];
        BinaryPrimitives.WriteUInt16LittleEndian(envelope, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(envelope[sizeof(ushort)..], originalLength);
        envelope[^1] = 0x42;
        frames.Write(envelope);
        ProtocolV2FrameWriter.EndFrame(frames, data);
        var complete = ProtocolV2FrameWriter.BeginFrame(frames, ProtocolV2FrameType.StreamComplete,
            ProtocolV2FrameFlags.None, unchecked((ulong)requestId));
        frames.Write(envelope[..sizeof(ushort)]);
        ProtocolV2FrameWriter.EndFrame(frames, complete);
        return frames;
    }

    private static (int Updates, long Credit) ReadTerminalWindowUpdates(PipeReader output,
        SharpLinkProtocolOptions limits, long requestId)
    {
        if (!output.TryRead(out var read))
            return (0, 0);
        try
        {
            var remaining = read.Buffer;
            var updates = 0;
            long credit = 0;
            while (ProtocolV2FrameParser.TryReadFrame(ref remaining, limits, out var header, out var payload))
            {
                Ensure(header.Type == ProtocolV2FrameType.WindowUpdate && header.RequestId == unchecked((ulong)requestId),
                    "terminal input must emit only the original stream's credit update");
                var update = ProtocolV2PayloadCodec.ReadWindowUpdate(payload);
                Ensure(update.StreamId == 1, "credit must stay on the existing stream route");
                updates++;
                credit += update.Credit;
            }
            Ensure(remaining.IsEmpty, "all flushed output frames must be complete");
            return (updates, credit);
        }
        finally { output.AdvanceTo(read.Buffer.End); }
    }

    private sealed class TerminalLoopCompressionProvider : ISharpLinkCompressionProvider
    {
        public string WireProfile => "test.terminal-loop/v1";
        internal Action? BeforeOutput { get; set; }
        internal int DecompressCount { get; private set; }
        internal bool FailDecode { get; init; }
        internal IRpcByteBufferWriter? DecodedOwner { get; private set; }
        public bool TryCompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes,
            CancellationToken cancellationToken = default) => false;
        public void Decompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            DecompressCount++;
            DecodedOwner = (IRpcByteBufferWriter)output;
            BeforeOutput?.Invoke();
            output.Write(new byte[maxOutputBytes]);
            if (FailDecode)
                throw new InvalidDataException("controlled decode failure after call terminal");
        }
    }

    private sealed class TerminalLoopRecordingDispatcher : IStreamDispatcher
    {
        internal int DispatchCount { get; private set; }
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
        {
            DispatchCount++;
            return ValueTask.CompletedTask;
        }
        public void Complete(bool isError, string? errorMessage) { }
        public void Complete(Exception? exception) { }
    }
}
