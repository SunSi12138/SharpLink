namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private const int MaxPersistentDecodeWorkers = 4;
    private const int MinimumPersistentDecodeQueueCapacity = 32;
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

    private ServerDecodeExecutor DecodeExecutor
        => Volatile.Read(ref _decodeExecutor) ?? throw new InvalidOperationException(
            "The server decode executor is unavailable because compression is not configured or the server has not started.");

    internal int DecodeWorkerCountForDiagnostics
        => Volatile.Read(ref _decodeExecutor)?.WorkerCount ?? 0;

    internal int DecodeQueueDepthForDiagnostics
        => Volatile.Read(ref _decodeExecutor)?.QueueDepth ?? 0;

    internal int DecodeSkippedBeforeStartForDiagnostics
        => Volatile.Read(ref _decodeExecutor)?.SkippedBeforeStart ?? 0;
}
