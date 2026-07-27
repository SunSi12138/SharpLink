namespace SharpLink.Runtime;

/// <summary>Creates the client side of one anonymous-pipe offer.</summary>
public sealed class AnonymousPipeClientTransportFactory : IClientTransportFactory
{
    private readonly string _inHandle;
    private readonly string _outHandle;
    private int _connectStarted;
    private int _disposed;

    /// <summary>Creates a one-shot factory for one pair of inherited anonymous-pipe handles.</summary>
    /// <param name="inHandle">The handle from which the client reads.</param>
    /// <param name="outHandle">The handle to which the client writes.</param>
    public AnonymousPipeClientTransportFactory(string inHandle, string outHandle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inHandle);
        ArgumentException.ThrowIfNullOrWhiteSpace(outHandle);
        _inHandle = inHandle;
        _outHandle = outHandle;
    }

    /// <inheritdoc />
    /// <remarks>The offer is consumed when the first connection attempt begins, even if that attempt fails.</remarks>
    public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _connectStarted, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "An anonymous-pipe offer can be connected only once; request a new offer to reconnect.");
        }

        AnonymousPipeClientStream? input = null;
        AnonymousPipeClientStream? output = null;
        try
        {
            input = new AnonymousPipeClientStream(PipeDirection.In, _inHandle);
            output = new AnonymousPipeClientStream(PipeDirection.Out, _outHandle);
            return new AnonymousPipeTransportConnection(input, output);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (input is not null)
                await input.DisposeAsync().ConfigureAwait(false);
            if (output is not null)
                await output.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Volatile.Write(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Allocates bounded anonymous-pipe offers and accepts their server connections.</summary>
public sealed class AnonymousPipeServerTransportListener : IServerTransportListener, IAnonymousPipeAllocator
{
    private const int DefaultOfferQueueCapacity = 1024;
    private readonly Channel<ITransportConnection> _offers;
    private readonly int _offerQueueCapacity;
    private readonly CancellationTokenSource _disposeCts = new();
    private Task? _disposeTask;
    private int _disposed;

    /// <summary>Creates an anonymous-pipe listener.</summary>
    /// <param name="offerQueueCapacity">Maximum unaccepted offers.</param>
    public AnonymousPipeServerTransportListener(int offerQueueCapacity = DefaultOfferQueueCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(offerQueueCapacity);
        _offerQueueCapacity = offerQueueCapacity;
        _offers = Channel.CreateBounded<ITransportConnection>(new BoundedChannelOptions(offerQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    /// <inheritdoc />
    public EndPoint? LocalEndPoint => null;

    /// <inheritdoc />
    public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        try
        {
            return await _offers.Reader.ReadAsync(acceptCts.Token).ConfigureAwait(false);
        }
        catch (ChannelClosedException) when (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(AnonymousPipeServerTransportListener));
        }
    }

    /// <inheritdoc />
    public async ValueTask<AnonymousPipeOffer> AllocateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        AnonymousPipeServerStream? input = null;
        AnonymousPipeServerStream? output = null;
        ITransportConnection? connection = null;
        try
        {
            input = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            output = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            var offer = new AnonymousPipeOffer(
                output.GetClientHandleAsString(),
                input.GetClientHandleAsString());
            connection = new AnonymousPipeTransportConnection(input, output);
            input = null;
            output = null;

            if (_offers.Writer.TryWrite(connection))
                return offer;

            await connection.DisposeAsync().ConfigureAwait(false);
            connection = null;
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(AnonymousPipeServerTransportListener));
            throw new SharpLinkException(
                SharpLinkErrorCode.ResourceExhausted,
                $"Anonymous-pipe offer queue reached its {_offerQueueCapacity}-connection limit.");
        }
        catch
        {
            if (input is not null)
                await input.DisposeAsync().ConfigureAwait(false);
            if (output is not null)
                await output.DisposeAsync().ConfigureAwait(false);
            if (connection is not null)
                await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_offers)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);
            if (Volatile.Read(ref _disposed) != 0)
                return ValueTask.CompletedTask;

            var operation = DisposeCoreAsync();
            if (operation.IsCompletedSuccessfully)
                return operation;
            _disposeTask = operation.AsTask();
            return new ValueTask(_disposeTask);
        }
    }

    private async ValueTask DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        Exception? cleanupException = null;
        try
        {
            _disposeCts.Cancel();
        }
        catch (Exception exception)
        {
            cleanupException = exception;
        }
        _offers.Writer.TryComplete();
        while (_offers.Reader.TryRead(out var connection))
        {
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupException = StreamTransportConnection.CombineCleanupExceptions(
                    cleanupException,
                    exception);
            }
        }
        try
        {
            _disposeCts.Dispose();
        }
        catch (Exception exception)
        {
            cleanupException = StreamTransportConnection.CombineCleanupExceptions(
                cleanupException,
                exception);
        }

        if (cleanupException is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupException).Throw();
    }
}
