using System.Text;
using PipeOptions = System.IO.Pipes.PipeOptions;

namespace SharpLink.Runtime;

/// <summary>Creates independent same-user, same-machine shared-memory connections.</summary>
public sealed class SharedMemoryClientTransportFactory : IClientTransportFactory, IPerformanceProfileAwareTransport
{
    private readonly string _pipeName;
    private readonly SharedMemoryTransportOptions _options;
    private readonly CancellationTokenSource _disposeCts = new();
    private SharpLinkPerformanceProfile _profile = SharpLinkPerformanceProfile.Balanced;
    private int _started;
    private int _disposed;

    /// <summary>Creates a shared-memory client factory for a local logical endpoint.</summary>
    public SharedMemoryClientTransportFactory(
        string name,
        SharedMemoryTransportOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _pipeName = NamedPipeName.Normalize($"shm-{name}");
        _options = (options ?? new SharedMemoryTransportOptions()).CloneValidated();
    }

    /// <inheritdoc />
    public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Volatile.Write(ref _started, 1);
        var resolved = _options.Resolve(_profile);
        using var timeoutCts = new CancellationTokenSource(resolved.HandshakeTimeout);
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token,
            _disposeCts.Token);

        var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        SharedMemoryMapping? mapping = null;
        try
        {
            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
            var nonce = new byte[SharedMemoryLayout.NonceBytes];
            System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
            await SharedMemoryHandshake.WriteClientHelloAsync(
                pipe,
                resolved.CapacityPerDirectionBytes,
                nonce,
                connectCts.Token).ConfigureAwait(false);
            var response = await SharedMemoryHandshake.ReadServerResponseAsync(
                pipe,
                nonce,
                connectCts.Token).ConfigureAwait(false);
            if (response.Capacity > resolved.CapacityPerDirectionBytes)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.FailedPrecondition,
                    "Shared-memory server selected a capacity larger than the client request.");
            }
            mapping = SharedMemoryMapping.OpenClient(response.Path, response.Capacity, nonce);
            await SharedMemoryHandshake.WriteClientAckAsync(pipe, nonce, connectCts.Token).ConfigureAwait(false);

            var control = new SharedMemoryControlChannel(pipe);
            pipe = null!;
            var connection = SharedMemoryTransportConnection.Create(
                mapping,
                control,
                isClient: true,
                resolved.SpinCount);
            mapping = null;
            return connection;
        }
        catch (OperationCanceledException exception) when (
            timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested &&
            !_disposeCts.IsCancellationRequested)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.Unavailable,
                $"Shared-memory transport handshake timed out after {resolved.HandshakeTimeout}.",
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.PermissionDenied,
                "Shared-memory transport could not access the same-user mapping.",
                exception);
        }
        finally
        {
            if (mapping is not null)
                await mapping.DisposeAsync().ConfigureAwait(false);
            if (pipe is not null)
                await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    void IPerformanceProfileAwareTransport.BindPerformanceProfile(SharpLinkPerformanceProfile profile)
    {
        if (Volatile.Read(ref _started) != 0)
            throw new InvalidOperationException("The shared-memory profile must be bound before connecting.");
        _profile = profile;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _disposeCts.Cancel();
            _disposeCts.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>Accepts independent same-user, same-machine shared-memory connections.</summary>
public sealed class SharedMemoryServerTransportListener : IServerTransportListener, IPerformanceProfileAwareTransport
{
    private readonly string _pipeName;
    private readonly SharedMemoryTransportOptions _options;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Lock _gate = new();
    private readonly HashSet<NamedPipeServerStream> _pendingAccepts = [];
    private Task? _disposeTask;
    private SharpLinkPerformanceProfile _profile = SharpLinkPerformanceProfile.Balanced;
    private int _started;
    private int _disposed;

    /// <summary>Creates a shared-memory server listener for a local logical endpoint.</summary>
    public SharedMemoryServerTransportListener(
        string name,
        SharedMemoryTransportOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _pipeName = NamedPipeName.Normalize($"shm-{name}");
        _options = (options ?? new SharedMemoryTransportOptions()).CloneValidated();
    }

    /// <inheritdoc />
    public EndPoint? LocalEndPoint => null;

    /// <inheritdoc />
    public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Volatile.Write(ref _started, 1);
        var resolved = _options.Resolve(_profile);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var connection = await AcceptConnectionAsync(resolved, cancellationToken).ConfigureAwait(false);
            if (connection is not null)
                return connection;
        }
    }

    private async ValueTask<ITransportConnection?> AcceptConnectionAsync(
        SharedMemoryResolvedOptions resolved,
        CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        if (!TryRegisterPending(pipe))
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(SharedMemoryServerTransportListener));
        }

        SharedMemoryMapping? mapping = null;
        string? path = null;
        CancellationTokenSource? timeoutCts = null;
        var handshakeStage = HandshakeStage.Accepting;
        using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCts.Token);
        try
        {
            await pipe.WaitForConnectionAsync(acceptCts.Token).ConfigureAwait(false);
            RemovePending(pipe);
            handshakeStage = HandshakeStage.ClientHello;
            timeoutCts = new CancellationTokenSource(resolved.HandshakeTimeout);
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCts.Token,
                _disposeCts.Token);
            var hello = await SharedMemoryHandshake.ReadClientHelloAsync(pipe, handshakeCts.Token).ConfigureAwait(false);
            var capacity = Math.Min(resolved.CapacityPerDirectionBytes, hello.Capacity);
            handshakeStage = HandshakeStage.CreatingMapping;
            mapping = SharedMemoryMapping.CreateServer(capacity, hello.Nonce, out path);
            handshakeStage = HandshakeStage.ServerResponse;
            await SharedMemoryHandshake.WriteServerResponseAsync(
                pipe,
                capacity,
                path,
                hello.Nonce,
                handshakeCts.Token).ConfigureAwait(false);
            handshakeStage = HandshakeStage.ClientAck;
            await SharedMemoryHandshake.ReadClientAckAsync(pipe, hello.Nonce, handshakeCts.Token).ConfigureAwait(false);
            mapping.UnlinkAfterClientOpened();

            handshakeStage = HandshakeStage.Completed;
            var control = new SharedMemoryControlChannel(pipe);
            pipe = null!;
            var connection = SharedMemoryTransportConnection.Create(
                mapping,
                control,
                isClient: false,
                resolved.SpinCount);
            mapping = null;
            return connection;
        }
        catch (OperationCanceledException) when (
            timeoutCts?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested &&
            !_disposeCts.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (IsRejectedHandshake(handshakeStage, exception))
        {
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.PermissionDenied,
                "Shared-memory transport could not create a same-user mapping.",
                exception);
        }
        finally
        {
            timeoutCts?.Dispose();
            RemovePending(pipe);
            if (mapping is not null)
                await mapping.DisposeAsync().ConfigureAwait(false);
            if (pipe is not null)
                await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool IsRejectedHandshake(HandshakeStage stage, Exception exception)
    {
        if (stage is not (HandshakeStage.ClientHello or HandshakeStage.ServerResponse or HandshakeStage.ClientAck))
            return false;

        return exception is EndOfStreamException or IOException or SocketException ||
               exception is SharpLinkException
               {
                   Code: SharpLinkErrorCode.FailedPrecondition or SharpLinkErrorCode.ProtocolViolation
               };
    }

    void IPerformanceProfileAwareTransport.BindPerformanceProfile(SharpLinkPerformanceProfile profile)
    {
        if (Volatile.Read(ref _started) != 0)
            throw new InvalidOperationException("The shared-memory profile must be bound before accepting connections.");
        _profile = profile;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_pendingAccepts)
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
        NamedPipeServerStream[] pending;
        lock (_gate)
        {
            pending = [.. _pendingAccepts];
            _pendingAccepts.Clear();
        }
        foreach (var pipe in pending)
        {
            try
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (StreamTransportConnection.IsExpectedDisposeException(ex))
            {
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

    private bool TryRegisterPending(NamedPipeServerStream pipe)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return false;
            _pendingAccepts.Add(pipe);
            return true;
        }
    }

    private void RemovePending(NamedPipeServerStream? pipe)
    {
        if (pipe is null)
            return;
        lock (_gate)
            _pendingAccepts.Remove(pipe);
    }

    private enum HandshakeStage
    {
        Accepting,
        ClientHello,
        CreatingMapping,
        ServerResponse,
        ClientAck,
        Completed
    }
}

internal sealed class SharedMemoryTransportConnection : ITransportConnection
{
    private readonly SharedMemoryMapping _mapping;
    private readonly SharedMemoryControlChannel _control;
    private readonly Lock _disposeGate = new();
    private Task? _disposeTask;

    private SharedMemoryTransportConnection(
        SharedMemoryMapping mapping,
        SharedMemoryControlChannel control,
        PipeReader input,
        PipeWriter output)
    {
        _mapping = mapping;
        _control = control;
        Input = input;
        Output = output;
        Id = Guid.NewGuid().ToString("N");
    }

    public string Id { get; }
    public PipeReader Input { get; }
    public PipeWriter Output { get; }
    public EndPoint? LocalEndPoint => null;
    public EndPoint? RemoteEndPoint => null;

    public static SharedMemoryTransportConnection Create(
        SharedMemoryMapping mapping,
        SharedMemoryControlChannel control,
        bool isClient,
        int spinCount)
    {
        var inputDirection = SharedMemoryLayout.GetDirection(mapping, clientToServer: !isClient);
        var outputDirection = SharedMemoryLayout.GetDirection(mapping, clientToServer: isClient);
        SharpLinkTelemetry.RecordSharedMemoryConnection(isClient ? "client" : "server", inputDirection.Capacity);
        var input = new SharedMemoryPipeReader(inputDirection, control, spinCount);
        var output = new SharedMemoryPipeWriter(outputDirection, control, spinCount);
        control.RegisterPeerWaiterHandlers(
            output.OnPeerReaderArmed,
            input.OnPeerWriterArmed);
        return new SharedMemoryTransportConnection(
            mapping,
            control,
            input,
            output);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Exception? cleanupException = null;
        try
        {
            Output.Complete();
        }
        catch (Exception ex) when (StreamTransportConnection.IsExpectedDisposeException(ex) || ex is SharpLinkException)
        {
        }
        catch (Exception exception)
        {
            cleanupException = exception;
        }
        try
        {
            await Input.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (StreamTransportConnection.IsExpectedDisposeException(ex) || ex is SharpLinkException)
        {
        }
        catch (Exception exception)
        {
            cleanupException = StreamTransportConnection.CombineCleanupExceptions(cleanupException, exception);
        }
        try
        {
            await _control.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupException = StreamTransportConnection.CombineCleanupExceptions(cleanupException, exception);
        }
        try
        {
            await _mapping.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupException = StreamTransportConnection.CombineCleanupExceptions(cleanupException, exception);
        }

        if (cleanupException is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupException).Throw();
    }
}

internal static class SharedMemoryHandshake
{
    private static readonly Encoding SStrictUtf8 = new UTF8Encoding(false, true);
    private const int Magic = 0x53484D31;
    private const int Version = 3;
    private const int ClientHelloBytes = 4 + 4 + 4 + 4 + SharedMemoryLayout.NonceBytes;
    private const int ClientAckBytes = 4 + 4 + SharedMemoryLayout.NonceBytes;
    private const int ServerResponseHeaderBytes = 20 + SharedMemoryLayout.NonceBytes;
    private const int MaxPathBytes = 3072;

    public static async ValueTask WriteClientHelloAsync(
        PipeStream stream,
        int capacity,
        byte[] nonce,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[ClientHelloBytes];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), Version);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), capacity);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(12), Environment.ProcessId);
        nonce.CopyTo(buffer, 16);
        await WriteAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<SharedMemoryClientHello> ReadClientHelloAsync(
        PipeStream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[ClientHelloBytes];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        ValidatePreamble(buffer);
        var capacity = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(8));
        var processId = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(12));
        if (processId <= 0)
            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "Shared-memory client process information was invalid.");
        try
        {
            new SharedMemoryTransportOptions { CapacityPerDirectionBytes = capacity }.Validate();
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "Shared-memory client requested an invalid capacity.", exception);
        }
        return new SharedMemoryClientHello(
            capacity,
            processId,
            buffer.AsSpan(16, SharedMemoryLayout.NonceBytes).ToArray());
    }

    public static async ValueTask WriteServerResponseAsync(
        PipeStream stream,
        int capacity,
        string path,
        byte[] nonce,
        CancellationToken cancellationToken)
    {
        var pathBytes = Encoding.UTF8.GetBytes(path);
        if (pathBytes.Length is 0 or > MaxPathBytes)
            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "Shared-memory mapping path exceeded the handshake limit.");
        var buffer = new byte[ServerResponseHeaderBytes + pathBytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), Version);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), capacity);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(12), Environment.ProcessId);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), pathBytes.Length);
        nonce.CopyTo(buffer, 20);
        pathBytes.CopyTo(buffer, ServerResponseHeaderBytes);
        await WriteAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<SharedMemoryServerResponse> ReadServerResponseAsync(
        PipeStream stream,
        byte[] expectedNonce,
        CancellationToken cancellationToken)
    {
        var header = new byte[ServerResponseHeaderBytes];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        ValidatePreamble(header);
        var capacity = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        var processId = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        var pathLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));
        if (pathLength is <= 0 or > MaxPathBytes ||
            processId <= 0 ||
            !header.AsSpan(20, SharedMemoryLayout.NonceBytes).SequenceEqual(expectedNonce))
        {
            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "Shared-memory server response failed validation.");
        }
        try
        {
            new SharedMemoryTransportOptions { CapacityPerDirectionBytes = capacity }.Validate();
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "Shared-memory server selected an invalid capacity.", exception);
        }

        var pathBytes = new byte[pathLength];
        await stream.ReadExactlyAsync(pathBytes, cancellationToken).ConfigureAwait(false);
        string path;
        try
        {
            path = SStrictUtf8.GetString(pathBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.FailedPrecondition,
                "Shared-memory mapping path is not valid UTF-8.",
                exception);
        }
        SharedMemoryMapping.ValidateMappingPath(path);
        return new SharedMemoryServerResponse(capacity, processId, path);
    }

    public static ValueTask WriteClientAckAsync(
        PipeStream stream,
        byte[] nonce,
        CancellationToken cancellationToken)
    {
        var buffer = CreateAck(nonce);
        return WriteAsync(stream, buffer, cancellationToken);
    }

    public static async ValueTask ReadClientAckAsync(
        PipeStream stream,
        byte[] expectedNonce,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[ClientAckBytes];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        ValidatePreamble(buffer);
        if (!buffer.AsSpan(8, SharedMemoryLayout.NonceBytes).SequenceEqual(expectedNonce))
            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "Shared-memory client acknowledgement nonce did not match.");
    }

    private static byte[] CreateAck(byte[] nonce)
    {
        var buffer = new byte[ClientAckBytes];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), Version);
        nonce.CopyTo(buffer, 8);
        return buffer;
    }

    private static void ValidatePreamble(ReadOnlySpan<byte> buffer)
    {
        if (BinaryPrimitives.ReadInt32LittleEndian(buffer) != Magic ||
            BinaryPrimitives.ReadInt32LittleEndian(buffer[4..]) != Version)
        {
            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "Shared-memory handshake version is unsupported.");
        }
    }

    private static async ValueTask WriteAsync(
        PipeStream stream,
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal readonly record struct SharedMemoryClientHello(int Capacity, int ProcessId, byte[] Nonce);
internal readonly record struct SharedMemoryServerResponse(int Capacity, int ProcessId, string Path);
