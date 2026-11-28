using System.Collections.Concurrent;
using System.IO.Pipelines;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyApprovedChangeFencesActuallyQueuedOldHeadsBeforeReady(string transport)
    {
        var probe = new NStudyOutboundProbe();
        return WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: false,
            (fixture, trace, ct) => VerifyNStudyOutboundFenceAsync(fixture, trace, probe, ct),
            factory => new NStudyFenceTransportFactory(factory, probe));
    }

    private static async Task VerifyNStudyOutboundFenceAsync(NStudyCoordinationFixture fixture,
        NStudyTrace trace, NStudyOutboundProbe probe, CancellationToken ct)
    {
        var (server, contracts, oldAssembly, newAssembly, oldType, newType, first, _, _) = fixture;
        var (client, session, peer, a1, b1, receiver, b) = first;
        var contract = contracts.ContractType;
        var a = receiver.Service;
        var originalRoute = session.CaptureNStudyRoute(a1.Contract);
        var priorBinds = session.NStudyBindOperationForDiagnostics;
        var vote = new NStudyManualDecision();
        var deferred = new NStudyDeferredFenceInvocation();
        var activeOriginal = InvokeValueTaskAsync<int>(a, contract, "BlockAsync", ct).AsTask();
        try
        {
            // Already admitted business may outlive the head fence and the update.
            await ReadNStudyTask(oldType, "BlockStarted").WaitAsync(ct);
            var reply = client.NegotiateNStudyReplacementAsync(NStudyInitiator.Server, vote.DecideAsync, ct);
            var replacement = server.StageNStudyReplacementAsync(peer, oldAssembly, newAssembly, a1.Contract,
                NStudyInitiator.Server, static (_, _) => ValueTask.FromResult(true), TimeSpan.FromSeconds(15), ct);
            var change = await vote.Seen.WaitAsync(ct);

            await WaitForNStudyAsync(() => session.QueuedSendBytes == 0, ct);
            probe.Arm(b1.BindingId);
            var blockingB = b.TouchAsync(1, ct).AsTask();
            await probe.FlushEntered.Task.WaitAsync(ct);
            var heldBytes = session.QueuedSendBytes;
            Ensure(heldBytes > 0 && !blockingB.IsCompleted, "the earlier B was selected, but its real transport flush is held");

            // The generated untimed OneWay can return once the head is actually enqueued.
            await InvokeValueTaskAsync(a, contract, "NotifyAsync", 17, ct);
            var acceptedBytes = session.QueuedSendBytes;
            Ensure(acceptedBytes > heldBytes && NStudyCount(oldType, "Notifications") == 0,
                "old OneWay is in the real queue behind the held B, not already at the peer");

            client.ReplaceInterceptors([deferred]);
            var stillBeforePublication = InvokeValueTaskAsync(a, contract, "NotifyAsync", 29, ct).AsTask();
            await deferred.Entered.Task.WaitAsync(ct);
            client.ReplaceInterceptors([]);

            var readyPayload = new ArrayBufferWriter<byte>();
            var module = GetDynamicModule(client, contracts.ContractAssembly);
            NStudyWire.WriteCoordination(readyPayload, new(NStudyPhase.Ready, change, module.NStudyRegistration));
            var readyBytes = ProtocolV2Constants.HeaderBytes + readyPayload.WrittenCount;
            vote.Approve();
            await WaitForNStudyAsync(() => !originalRoute.IsOpenOn(session) &&
                session.QueuedSendBytes == acceptedBytes + readyBytes, ct);
            Ensure(!reply.IsCompleted && !replacement.IsCompleted &&
                   server.ReadNStudyTarget(oldAssembly, a1.Contract) == a1.Target &&
                   NStudyCount(newType, "Created") == 0 && probe.Flushed.IsEmpty,
                "approval closes old enqueue but cannot be real on-wire Ready or remote activation before the writer drains");
            trace.Record("ApprovalQueuedReadyWhileOldHeadStillPhysicallyHeld", a1, b1);

            probe.Release();
            Ensure(await blockingB.WaitAsync(ct) == 101, "the ordinary earlier B frames leave the real writer");
            var resumed = await reply.WaitAsync(ct) ?? throw new InvalidOperationException("Expected exact activation.");
            var frames = probe.Flushed.ToArray();
            var oldIndex = Array.FindIndex(frames, frame => frame.Binding == a1.BindingId);
            var readyIndex = Array.FindIndex(frames, frame =>
                frame.Control is { Phase: NStudyPhase.Ready } control && control.Change == change);
            var bIndex = Array.FindIndex(frames, frame => frame.Binding == b1.BindingId);
            Ensure(bIndex >= 0 && oldIndex > bIndex && readyIndex > oldIndex &&
                   frames[oldIndex].Header.Flags.HasFlag(ProtocolV2FrameFlags.OneWay) &&
                   peer.LastAcceptedRequestId == unchecked((long)frames[oldIndex].Header.RequestId) &&
                   resumed.Change == change && resumed.Binding.Target == server.ReadNStudyTarget(newAssembly, a1.Contract) &&
                   resumed.Binding.Authority == a1.Authority && !activeOriginal.IsCompleted && !replacement.IsCompleted,
                "the same physical writer emits old head before Ready; receiver observes old RequestId before committing");
            trace.Record("PhysicalFifoEmitsActualOldHeadBeforeExactReady", a1, resumed.Binding);

            deferred.Release();
            try
            {
                await stillBeforePublication.WaitAsync(ct);
                throw new InvalidOperationException("Expected the captured old route to refuse late publication.");
            }
            catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.Unavailable) { }
            Ensure(await InvokeValueTaskAsync<int>(a, contract, "UnaryAsync", 5, ct) == 6 &&
                   await b.TouchAsync(2, ct) == 102 &&
                   probe.Flushed.Count(frame => frame.Binding == a1.BindingId) == 1 &&
                   NStudyCount(newType, "Notifications") == 0 &&
                   session.NStudyBindOperationForDiagnostics == priorBinds + 1 && ReferenceEquals(session, NStudyClientSession(client)),
                "a deferred old call cannot publish after Ready or become R2, while new A and unrelated B still work");
            trace.Record("DeferredOldInvocationCannotPublishBehindReady", a1, resumed.Binding);

            oldType.GetMethod("ReleaseBlock")!.Invoke(null, null);
            Ensure(await activeOriginal.WaitAsync(ct) == 42, "original physical R1 need not finish before Ready");
            EnsureReplacementReleased((await replacement.WaitAsync(ct)).Replacement, "R1 later drains once");
        }
        finally
        {
            vote.Approve();
            deferred.Release();
            probe.Release();
            oldType.GetMethod("ReleaseBlock")!.Invoke(null, null);
        }
    }

    private sealed class NStudyDeferredFenceInvocation : ISharpLinkClientInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release() => _release.TrySetResult();

        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context, SharpLinkClientInvocationDelegate next)
        {
            Entered.TrySetResult();
            await _release.Task;
            return await next(context);
        }
    }

    private sealed class NStudyOutboundProbe(Func<NStudyWriterFrame, bool>? holdControl = null)
    {
        private long _blockingBinding;
        private int _recording;
        internal bool IsRecording => Volatile.Read(ref _recording) != 0;
        internal long BlockingBinding => Volatile.Read(ref _blockingBinding);
        internal TaskCompletionSource FlushEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ContinueFlush { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<NStudyWriterFrame> Flushed { get; } = new();
        internal void Arm(long token)
        {
            Volatile.Write(ref _blockingBinding, token);
            StartRecording();
        }

        internal void StartRecording() => Volatile.Write(ref _recording, 1);
        internal void Release() => ContinueFlush.TrySetResult();
        internal bool ShouldHold(NStudyWriterFrame frame) =>
            (frame.Binding != 0 && frame.Binding == BlockingBinding) || holdControl?.Invoke(frame) == true;
    }

    private readonly record struct NStudyWriterFrame(
        ProtocolV2FrameHeader Header, long Binding, NStudyCoordinationMessage? Control);

    private sealed class NStudyFenceTransportFactory(IClientTransportFactory inner, NStudyOutboundProbe probe)
        : IClientTransportFactory
    {
        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => new NStudyFenceConnection(await inner.ConnectAsync(cancellationToken), probe);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class NStudyFenceListener(IServerTransportListener inner, NStudyOutboundProbe probe)
        : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => inner.LocalEndPoint;
        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => new NStudyFenceConnection(await inner.AcceptAsync(cancellationToken), probe);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class NStudyFenceConnection(ITransportConnection inner, NStudyOutboundProbe probe)
        : ITransportConnection
    {
        public string Id => inner.Id;
        public PipeReader Input => inner.Input;
        public PipeWriter Output { get; } = new NStudyFenceWriter(inner.Output, probe);
        public EndPoint? LocalEndPoint => inner.LocalEndPoint;
        public EndPoint? RemoteEndPoint => inner.RemoteEndPoint;
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    // Observe complete frames delivered to the real transport by RpcSession's writer.
    // Do not generate, re-order, modify or inject protocol bytes.
    private sealed class NStudyFenceWriter(PipeWriter inner, NStudyOutboundProbe probe) : PipeWriter
    {
        private readonly SharpLinkProtocolOptions _limits = new();
        private readonly List<NStudyWriterFrame> _batch = [];
        private Memory<byte> _writeBuffer;

        public override Memory<byte> GetMemory(int sizeHint = 0) => _writeBuffer = inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

        public override void Advance(int bytes)
        {
            if (probe.IsRecording)
            {
                var buffer = new ReadOnlySequence<byte>(_writeBuffer[..bytes]);
                while (!buffer.IsEmpty)
                {
                    Ensure(ProtocolV2FrameParser.TryReadFrame(ref buffer, _limits, out var header, out var payload),
                        "the real send pump supplies complete frames for this fixture");
                    var binding = 0L;
                    NStudyCoordinationMessage? control = null;
                    if (header.Type == ProtocolV2FrameType.Request &&
                        !header.Flags.HasFlag(ProtocolV2FrameFlags.Compressed))
                        binding = BinaryPrimitives.ReadInt64LittleEndian(payload.FirstSpan);
                    else if (header.Type == NStudyWire.Coordination)
                        control = NStudyWire.ReadCoordination(header.RequestId, payload);
                    _batch.Add(new(header, binding, control));
                }
            }

            inner.Advance(bytes);
            _writeBuffer = default;
        }

        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            if (_batch.Any(probe.ShouldHold) && probe.FlushEntered.TrySetResult())
                await probe.ContinueFlush.Task.WaitAsync(cancellationToken);
            var result = await inner.FlushAsync(cancellationToken);
            if (!result.IsCanceled && !result.IsCompleted)
                foreach (var frame in _batch)
                    probe.Flushed.Enqueue(frame);
            _batch.Clear();
            return result;
        }

        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
    }
}
