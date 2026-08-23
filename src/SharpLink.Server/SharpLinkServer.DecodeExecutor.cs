namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private const int MaxPersistentDecodeWorkers = 4;
    private const int MinimumPersistentDecodeQueueCapacity = 32;
    // Phase 0 has current-D performance evidence at 1 MiB. Smaller cutovers remain hypotheses until
    // real RequestLoop control-plane measurements are collected in this slice.
    private const int InitialPersistentDecodeThresholdBytes = 1024 * 1024;
    private ServerDecodeExecutor? _decodeExecutor;

    private void StartDecodeExecutor()
    {
        if (_runtimeContext.Compression.ProviderBindings.Count == 0)
            return;
        if (Volatile.Read(ref _decodeExecutor) is not null)
            throw new InvalidOperationException("The server decode executor was started more than once.");

        var flowControl = _runtimeContext.FlowControl;
        var workerCount = Math.Min(
            flowControl.MaxConcurrentDecodesPerServer,
            Math.Clamp(Environment.ProcessorCount, 1, MaxPersistentDecodeWorkers));
        var queueCapacity = Math.Max(
            MinimumPersistentDecodeQueueCapacity,
            checked(workerCount * 8));
        var executor = new ServerDecodeExecutor(workerCount, queueCapacity);
        Volatile.Write(ref _decodeExecutor, executor);
        _ = _forceStopCts.Token.UnsafeRegister(
            static state => ((ServerDecodeExecutor)state!).StopAccepting(),
            executor);
        TrackFrameworkTask(executor.Completion, "DecodeExecutor");
    }

    private bool ShouldUsePersistentDecode(
        ProtocolV2FrameFlags flags,
        ServiceRegistration serviceInfo,
        ServerRequestEnvelope request,
        ReadOnlySequence<byte> payload)
    {
        if ((flags & ProtocolV2FrameFlags.Compressed) == 0 ||
            (flags & ProtocolV2FrameFlags.Cancellable) == 0 ||
            Volatile.Read(ref _decodeExecutor) is null ||
            !serviceInfo.Stub.SupportsCancellation(request.MethodHash))
        {
            return false;
        }

        return RpcSession.ReadCompressedDecodedPayloadLength(
            ProtocolV2FrameType.Request,
            flags,
            payload) >= InitialPersistentDecodeThresholdBytes;
    }

    private ServerDecodeExecutor DecodeExecutor
        => Volatile.Read(ref _decodeExecutor) ?? throw new InvalidOperationException(
            "The server decode executor is unavailable because compression is not configured or the server has not started.");

    internal int DecodeWorkerCountForDiagnostics
        => Volatile.Read(ref _decodeExecutor)?.WorkerCount ?? 0;

    internal int DecodeQueueDepthForDiagnostics
        => Volatile.Read(ref _decodeExecutor)?.QueueDepth ?? 0;

    internal int DecodeSkippedBeforeStartForDiagnostics
        => Volatile.Read(ref _decodeExecutor)?.SkippedBeforeStart ?? 0;

    internal int DecodeStartedWorkCountForDiagnostics
        => Volatile.Read(ref _decodeExecutor)?.StartedWorkItems ?? 0;
}
