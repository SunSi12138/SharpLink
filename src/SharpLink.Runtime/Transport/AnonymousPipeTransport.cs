namespace SharpLink.Runtime;

public sealed class AnonymousPipeTransport : ITransport, IRpcSessionFlushConfigurableTransport
{
    private enum Mode
    {
        ClientHandles,
        ServerOffer
    }

    private readonly Mode _mode;
    private readonly Func<AnonymousPipeOffer, CancellationToken, ValueTask>? _onOffer;
    private readonly string? _inHandle;
    private readonly string? _outHandle;
    private readonly TimeSpan _offerTimeout;
    private readonly Lock _gate = new();
    private readonly HashSet<PipeStream> _activePipes = [];
    private long _nextConnectionId;
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
        _offerTimeout = Timeout.InfiniteTimeSpan;
    }

    public AnonymousPipeTransport(
        Func<AnonymousPipeOffer, CancellationToken, ValueTask> onOffer,
        TimeSpan? offerTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(onOffer);
        if (offerTimeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(offerTimeout));

        _mode = Mode.ServerOffer;
        _onOffer = onOffer;
        _offerTimeout = offerTimeout ?? Timeout.InfiniteTimeSpan;
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

    private IRpcSession ConnectClient()
    {
        var input = new AnonymousPipeClientStream(PipeDirection.In, _inHandle!);
        var output = new AnonymousPipeClientStream(PipeDirection.Out, _outHandle!);
        RegisterStreams(input, output);
        return CreateSession(input, output);
    }

    private async Task<IRpcSession> ConnectServerAsync(CancellationToken ct)
    {
        var input = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var output = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);

        var inHandle = output.GetClientHandleAsString();
        var outHandle = input.GetClientHandleAsString();
        Interlocked.Increment(ref _nextConnectionId);
        var offer = new AnonymousPipeOffer(
            InHandle: inHandle,
            OutHandle: outHandle);

        try
        {
            await _onOffer!(offer, ct);
            await WaitForConnectedAsync(input, output, ct);

            RegisterStreams(input, output);
            return CreateSession(input, output);
        }
        catch
        {
            try
            {
                input.DisposeLocalCopyOfClientHandle();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
            }

            try
            {
                output.DisposeLocalCopyOfClientHandle();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
            }

            input.Dispose();
            output.Dispose();
            throw;
        }
    }

    private async Task WaitForConnectedAsync(
        AnonymousPipeServerStream input,
        AnonymousPipeServerStream output,
        CancellationToken ct)
    {
        if (input.IsConnected && output.IsConnected)
            return;

        using var timeoutCts = _offerTimeout == Timeout.InfiniteTimeSpan
            ? null
            : new CancellationTokenSource(_offerTimeout);
        using var linkedCts = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var token = linkedCts.Token;

        while (!input.IsConnected || !output.IsConnected)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(10, token);
        }
    }

    private RpcSession CreateSession(PipeStream input, PipeStream output)
    {
        return new RpcSession(
            Guid.NewGuid().ToString("N"),
            PipeReader.Create(input),
            PipeWriter.Create(output),
            () => ReleaseStreams(input, output),
            () => !_disposed && input.IsConnected && output.IsConnected,
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
}
