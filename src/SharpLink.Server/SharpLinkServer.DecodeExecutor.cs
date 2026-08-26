namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private const int MaxPersistentDecodeWorkers = 4;
    private const int MinimumPersistentDecodeQueueCapacity = 32;
    // Phase 0 has current-D performance evidence at 1 MiB decoded size. The same conservative
    // bound also caps synchronous compressed-input work on the RequestLoop: built-in Brotli scans
    // the complete compressed body for integrity before its cancellable decode loop.
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
        _ = _acceptCts.Token.UnsafeRegister(
            static state => ((ServerDecodeExecutor)state!).StopAccepting(),
            executor);
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
        _ = serviceInfo;
        _ = request;
        if ((flags & ProtocolV2FrameFlags.Compressed) == 0 ||
            (flags & ProtocolV2FrameFlags.Cancellable) == 0 ||
            Volatile.Read(ref _decodeExecutor) is null)
        {
            return false;
        }

        var decodedPayloadBytes = RpcSession.ReadCompressedDecodedPayloadLength(
            ProtocolV2FrameType.Request,
            flags,
            payload);

        // Execution location is a pre-invocation decode decision. It is intentionally independent
        // from whether the eventual service handler consumes a cancellation token. Include both
        // output work and compressed-input work so a small declared output cannot force a large
        // synchronous provider pre-scan onto the RequestLoop.
        return decodedPayloadBytes >= InitialPersistentDecodeThresholdBytes ||
               payload.Length >= InitialPersistentDecodeThresholdBytes;
    }

    private ServerDecodeExecutor DecodeExecutor
        => Volatile.Read(ref _decodeExecutor) ?? throw new InvalidOperationException(
            "The server decode executor is unavailable because compression is not configured or the server has not started.");

    internal int DecodeWorkerCountForDiagnostics
        => Volatile.Read(ref _decodeExecutor)?.WorkerCount ?? 0;

    internal int DecodeQueueDepthForDiagnostics
        => Volatile.Read(ref _decodeExecutor)?.QueueDepth ?? 0;

    internal int DecodeQueueReservationsForDiagnostics
        => Volatile.Read(ref _decodeExecutor)?.QueueReservations ?? 0;

    internal int DecodeSkippedBeforeStartForDiagnostics
        => Volatile.Read(ref _decodeExecutor)?.SkippedBeforeStart ?? 0;

    internal int DecodeStartedWorkCountForDiagnostics
        => Volatile.Read(ref _decodeExecutor)?.StartedWorkItems ?? 0;

    internal bool DecodeAcceptingForDiagnostics
        => Volatile.Read(ref _decodeExecutor)?.IsAccepting ?? false;
}
