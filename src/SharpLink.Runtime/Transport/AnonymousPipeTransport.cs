namespace SharpLink.Runtime;

public sealed class AnonymousPipeTransport : ITransport, IRpcSessionFlushConfigurableTransport,IAnonymousPipeAllocator
{
    private enum Mode
    {
        ClientHandles,
        ServerOffer
    }

    private readonly Mode _mode;
    private readonly string? _inHandle;
    private readonly string? _outHandle;
    private readonly Lock _gate = new();
    private readonly HashSet<PipeStream> _activePipes = [];
    private readonly Channel<AnonymousPipeDuplexStream> _sessionQueue = Channel.CreateUnbounded<AnonymousPipeDuplexStream>();
    private bool _disposed;
    private RpcSessionFlushOptions? _rpcSessionFlushOptions;

    public AnonymousPipeTransport(
        string inHandle,
        string outHandle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inHandle);
        ArgumentException.ThrowIfNullOrWhiteSpace(outHandle);

        _mode = Mode.ClientHandles;
        _inHandle = inHandle;
        _outHandle = outHandle;
    }

    public AnonymousPipeTransport()
    {
        _mode = Mode.ServerOffer;
    }

    public async Task<IRpcSession> ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _mode switch
        {
            Mode.ClientHandles => ConnectClient(),
            Mode.ServerOffer => await ConnectServerAsync(ct),
            _ => throw new InvalidOperationException("Unknown anonymous pipe mode.")
        };
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

        List<PipeStream> streams;
        lock (_gate)
        {
            streams = [.. _activePipes];
            _activePipes.Clear();
        }

        foreach (var stream in streams)
        {
            try
            {
                stream.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or ArgumentException)
            {
            }
        }

    }

    private RpcSession ConnectClient()
    {
        var input = new AnonymousPipeClientStream(PipeDirection.In, _inHandle!);
        var output = new AnonymousPipeClientStream(PipeDirection.Out, _outHandle!);
        RegisterStreams(input, output);
        return CreateSession(input, output);
    }

    private async Task<RpcSession> ConnectServerAsync(CancellationToken ct = default)
    {
        var sessionStream = await _sessionQueue.Reader.ReadAsync(ct);
        var session = CreateSession(sessionStream.Input, sessionStream.Output);
        RegisterStreams(sessionStream.Input,sessionStream.Output);
        session.OnConnected += () =>
        {
            sessionStream.Input.DisposeLocalCopyOfClientHandle();
            sessionStream.Output.DisposeLocalCopyOfClientHandle();
        };
        return session;
    }
    
    private RpcSession CreateSession(PipeStream input, PipeStream output)
    {
        return new RpcSession(
            Guid.NewGuid().ToString("N"),
            PipeReader.Create(input),
            PipeWriter.Create(output),
            () => ReleaseStreams(input, output),
            () => !_disposed&&input.IsConnected && output.IsConnected,
            _rpcSessionFlushOptions);
    }

    private void RegisterStreams(PipeStream input, PipeStream output)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                input.Dispose();
                output.Dispose();
                throw new ObjectDisposedException(nameof(AnonymousPipeTransport));
            }

            _activePipes.Add(input);
            _activePipes.Add(output);
        }
    }

    private void ReleaseStreams(PipeStream input, PipeStream output)
    {
        lock (_gate)
        {
            _activePipes.Remove(input);
            _activePipes.Remove(output);
        }

        try
        {
            input.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or ArgumentException)
        {
        }

        try
        {
            output.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or ArgumentException)
        {
        }
    }

    public (string InHandle, string OutHandle) AllocateNewSession()
    {
        
        var input = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var output = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);

        var inHandle = output.GetClientHandleAsString();
        var outHandle = input.GetClientHandleAsString();
        _sessionQueue.Writer.TryWrite(new AnonymousPipeDuplexStream(input, output));
        
        return (inHandle, outHandle);
    }
    
    private record AnonymousPipeDuplexStream(AnonymousPipeServerStream Input, AnonymousPipeServerStream Output);
}
