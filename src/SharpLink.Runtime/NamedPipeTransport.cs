namespace SharpLink.Runtime;

public class NamedPipeTransport(NamedPipeServerStream? serverStream=null,NamedPipeClientStream? clientStream=null) : ITransport, IRpcSessionFlushConfigurableTransport
{
    private readonly PipeStream _pipe = ((PipeStream?)serverStream ?? clientStream ?? throw new ArgumentNullException());
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;
    private RpcSessionFlushOptions? _rpcSessionFlushOptions;
    

    public bool IsConnected => _pipe is { IsConnected: true };
    public PipeReader Input => field??=PipeReader.Create(_pipe);
    public PipeWriter Output => field??=PipeWriter.Create(_pipe);

    public async Task<IRpcSession> ConnectAsync(ISerializer serializer,CancellationToken ct=default)
    {
        switch (_pipe)
        {
            case NamedPipeClientStream client:
                await client.ConnectAsync(ct);
                break;
            case NamedPipeServerStream server:
                await server.WaitForConnectionAsync(ct);
                break;
        }
        
        return new RpcSession(
            Guid.NewGuid().ToString("N"),
            PipeReader.Create(_pipe),
            PipeWriter.Create(_pipe),
            serializer,
            _pipe.Close,
            () => _pipe.IsConnected,
            _rpcSessionFlushOptions);
    }

    public void ConfigureRpcSessionFlush(RpcSessionFlushOptions options)
    {
        RpcSessionFlushOptions.Validate(options.FlushSizeThreshold, options.MaxLatency);
        _rpcSessionFlushOptions = options;
    }

    public Task CompleteAsync()
    {
        _pipe.Close();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, true))
            return;

        _cts.Cancel();
        _pipe.Dispose();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
