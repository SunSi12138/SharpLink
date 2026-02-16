namespace SharpLink.Runtime;

public sealed partial class RpcSession : IRpcSession
{
    public string Id { get; }
    public DateTime LastActive { get; set; } = DateTime.UtcNow;
    public PipeReader Input { get; }
    public ISerializer Serializer { get; }
    private PipeWriter Output { get; }

    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public IStreamManager StreamManager { get; } = new StreamManager();
    public bool IsConnected => _isConnected();
    private readonly Action _disconnect;
    private readonly Func<bool> _isConnected;

    private readonly SendPump _pump;

    public RpcSession(
        string id,
        PipeReader reader,
        PipeWriter writer,
        ISerializer serializer,
        Action disconnect,
        Func<bool> isConnected,
        RpcSessionFlushOptions? flushOptions = null)
    {
        var effectiveFlushOptions = flushOptions ?? RpcSessionFlushOptions.Default;
        RpcSessionFlushOptions.Validate(effectiveFlushOptions.FlushSizeThreshold, effectiveFlushOptions.MaxLatency);

        Id = id;
        Input = reader;
        Output = writer;
        Serializer = serializer;

        _disconnect = disconnect;
        _isConnected = isConnected;

        _pump = new SendPump(
            output: writer,
            flushSizeThreshold: effectiveFlushOptions.FlushSizeThreshold,
            maxLatency: effectiveFlushOptions.MaxLatency,
            cts: _cts);
    }

    private int _droppedCount;

    public void SendPacket(ArrayBufferWriter<byte> packet)
    {
        if (Volatile.Read(ref _disposed))
        {
            BufferWriterPool.Return(packet);
            Interlocked.Increment(ref _droppedCount);
            return;
        }

        _pump.Enqueue(packet);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, true))
            return;

        _cts.Cancel();

        // Stop accepting new packets and return queued buffers.
        _pump.Dispose();
        await _pump.WaitForStopAsync().ConfigureAwait(false);

        try
        {
            await Output.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or ArgumentNullException)
        {
            // Transport can be concurrently torn down during shutdown.
        }

        _disconnect();
        _cts.Dispose();

        if (_droppedCount > 0)
        {
            // Pending sends can race with shutdown; buffers were already returned in SendPacket.
        }
    }
}
