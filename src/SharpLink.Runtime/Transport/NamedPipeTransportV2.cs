using PipeOptions = System.IO.Pipes.PipeOptions;

namespace SharpLink.Runtime;

/// <summary>Creates independent named-pipe client connections.</summary>
public sealed class NamedPipeClientTransportFactory : IClientTransportFactory
{
    private readonly string _pipeName;
    private readonly string _serverName;
    private readonly PipeOptions _pipeOptions;
    private readonly CancellationTokenSource _disposeCts = new();
    private int _disposed;

    /// <summary>Creates a named-pipe client factory.</summary>
    /// <param name="pipeName">The logical pipe name.</param>
    /// <param name="serverName">The remote server name; use a dot for the local machine.</param>
    /// <param name="pipeOptions">Operating-system pipe options.</param>
    public NamedPipeClientTransportFactory(
        string pipeName,
        string serverName = ".",
        PipeOptions pipeOptions = PipeOptions.Asynchronous)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        _pipeName = NamedPipeName.Normalize(pipeName);
        _serverName = serverName;
        _pipeOptions = pipeOptions;
    }

    /// <inheritdoc />
    public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        var pipe = new NamedPipeClientStream(
            _serverName,
            _pipeName,
            PipeDirection.InOut,
            _pipeOptions);
        try
        {
            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
            return new StreamTransportConnection(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
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

/// <summary>Accepts independent named-pipe server connections.</summary>
public sealed class NamedPipeServerTransportListener : IServerTransportListener
{
    private readonly string _pipeName;
    private readonly int _maxServerInstances;
    private readonly PipeTransmissionMode _transmissionMode;
    private readonly PipeOptions _pipeOptions;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Lock _gate = new();
    private readonly HashSet<NamedPipeServerStream> _pendingAccepts = [];
    private int _disposed;

    /// <summary>Creates a named-pipe listener.</summary>
    /// <param name="pipeName">The logical pipe name.</param>
    /// <param name="maxServerInstances">Maximum operating-system pipe instances.</param>
    /// <param name="transmissionMode">Byte or message transmission mode.</param>
    /// <param name="pipeOptions">Operating-system pipe options.</param>
    public NamedPipeServerTransportListener(
        string pipeName,
        int maxServerInstances = NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode transmissionMode = PipeTransmissionMode.Byte,
        PipeOptions pipeOptions = PipeOptions.Asynchronous)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentOutOfRangeException.ThrowIfZero(maxServerInstances);
        _pipeName = NamedPipeName.Normalize(pipeName);
        _maxServerInstances = maxServerInstances;
        _transmissionMode = transmissionMode;
        _pipeOptions = pipeOptions;
    }

    /// <inheritdoc />
    public EndPoint? LocalEndPoint => null;

    /// <inheritdoc />
    public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var pipe = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            _maxServerInstances,
            _transmissionMode,
            _pipeOptions);
        if (!TryRegisterPending(pipe))
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(NamedPipeServerTransportListener));
        }
        try
        {
            using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
            await pipe.WaitForConnectionAsync(acceptCts.Token).ConfigureAwait(false);
            RemovePending(pipe);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return new StreamTransportConnection(pipe);
        }
        catch
        {
            RemovePending(pipe);
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _disposeCts.Cancel();
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
        }

        _disposeCts.Dispose();
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

    private void RemovePending(NamedPipeServerStream pipe)
    {
        lock (_gate)
            _pendingAccepts.Remove(pipe);
    }
}

internal static class NamedPipeName
{
    private const int UnixDomainSocketPathLengthLimit = 103;
    private const string UnixNamedPipePrefix = "CoreFxPipe_";

    public static string Normalize(string pipeName)
    {
        if (OperatingSystem.IsWindows())
            return pipeName;

        var tempPath = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var maxPipeNameLength = UnixDomainSocketPathLengthLimit - tempPath.Length - 1 - UnixNamedPipePrefix.Length;
        if (maxPipeNameLength <= 0)
            throw new PlatformNotSupportedException("Current temporary directory leaves no room for Unix named pipe paths.");
        if (pipeName.Length <= maxPipeNameLength)
            return pipeName;

        var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(pipeName)))
            .ToLowerInvariant();
        var hashLength = Math.Min(hash.Length, maxPipeNameLength);
        var prefixLength = Math.Max(0, maxPipeNameLength - hashLength - 1);
        return prefixLength == 0
            ? hash[..hashLength]
            : $"{pipeName[..prefixLength]}-{hash[..hashLength]}";
    }
}
