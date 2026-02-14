namespace SharpLink.Runtime;

public class AnonymousPipeTransport(PipeStream inputStream, PipeStream outputStream) : ITransport, IRpcSessionFlushConfigurableTransport
{
    private readonly PipeStream _input = inputStream ?? throw new ArgumentNullException(nameof(inputStream));
    private readonly PipeStream _output = outputStream ?? throw new ArgumentNullException(nameof(outputStream));
    private bool _isConnected;
    private bool _disposed;
    private RpcSessionFlushOptions? _rpcSessionFlushOptions;

    public async Task<IRpcSession> ConnectAsync(ISerializer serializer, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _isConnected, true))
        {
            var tcs = new TaskCompletionSource<IRpcSession>();
            await using var res = ct.Register(() => tcs.TrySetCanceled());
            return await tcs.Task;
        }

        var reader = PipeReader.Create(_input);
        var writer = PipeWriter.Create(_output);

        return new RpcSession(
            Guid.NewGuid().ToString("N"),
            reader,
            writer,
            serializer,
            Dispose,
            () => _input.IsConnected && _output.IsConnected,
            _rpcSessionFlushOptions);
    }

    public void ConfigureRpcSessionFlush(RpcSessionFlushOptions options)
    {
        RpcSessionFlushOptions.Validate(options.FlushSizeThreshold, options.MaxLatency);
        _rpcSessionFlushOptions = options;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, true))
            return;

        _input.Dispose();
        _output.Dispose();
        GC.SuppressFinalize(this);
    }
}
