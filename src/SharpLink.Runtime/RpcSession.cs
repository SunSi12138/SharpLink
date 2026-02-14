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
            _droppedCount++;
            return;
        }

        _pump.Enqueue(packet);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, true))
            return;

        _cts.Cancel();

        _disconnect();

        // 清空队列归还 buffer，避免泄漏
        _pump.Dispose();

        Output.Complete();

        _cts.Dispose();
        
        if(_droppedCount > 0)
            throw new Exception($"Dropped {_droppedCount} packets");//暂时抛出异常测试，按理不应该在dispose以后继续写入
    }
}
