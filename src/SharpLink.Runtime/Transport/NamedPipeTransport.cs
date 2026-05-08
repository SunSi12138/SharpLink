using PipeOptions = System.IO.Pipes.PipeOptions;

namespace SharpLink.Runtime;

public sealed class NamedPipeTransport : ITransport, IRpcSessionFlushConfigurableTransport
{
    // Keep one byte of headroom for the underlying Unix domain socket path validation.
    private const int UnixDomainSocketPathLengthLimit = 103;
    private const string UnixNamedPipePrefix = "CoreFxPipe_";
    private readonly string _pipeName;
    private readonly bool _isServer;
    private readonly string _serverName;
    private readonly int _maxServerInstances;
    private readonly PipeTransmissionMode _transmissionMode;
    private readonly PipeOptions _pipeOptions;
    private readonly Lock _gate = new();
    private readonly HashSet<PipeStream> _activePipes = [];
    private bool _disposed;
    private RpcSessionFlushOptions? _rpcSessionFlushOptions;

    public NamedPipeTransport(
        string pipeName,
        bool isServer,
        string serverName = ".",
        int maxServerInstances = NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode transmissionMode = PipeTransmissionMode.Byte,
        PipeOptions pipeOptions = PipeOptions.Asynchronous)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentOutOfRangeException.ThrowIfZero(maxServerInstances);

        _pipeName = NormalizePipeName(pipeName);
        _isServer = isServer;
        _serverName = serverName;
        _maxServerInstances = maxServerInstances;
        _transmissionMode = transmissionMode;
        _pipeOptions = pipeOptions;
    }

    public async Task<IRpcSession> ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        PipeStream pipe;
        if (_isServer)
        {
            var serverPipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                _maxServerInstances,
                _transmissionMode,
                _pipeOptions);
            try
            {
                await serverPipe.WaitForConnectionAsync(ct);
            }
            catch
            {
                serverPipe.Dispose();
                throw;
            }

            pipe = serverPipe;
        }
        else
        {
            var clientPipe = new NamedPipeClientStream(
                _serverName,
                _pipeName,
                PipeDirection.InOut,
                _pipeOptions);
            try
            {
                await clientPipe.ConnectAsync(ct);
            }
            catch
            {
                clientPipe.Dispose();
                throw;
            }

            pipe = clientPipe;
        }

        RegisterPipe(pipe);

        return new RpcSession(
            Guid.NewGuid().ToString("N"),
            PipeReader.Create(pipe),
            PipeWriter.Create(pipe),
            () => ReleasePipe(pipe),
            () => !_disposed && pipe.IsConnected,
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

        List<PipeStream> pipes;
        lock (_gate)
        {
            pipes = [.. _activePipes];
            _activePipes.Clear();
        }

        foreach (var pipe in pipes)
        {
            try
            {
                pipe.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or ArgumentException)
            {
            }
        }
    }

    private void RegisterPipe(PipeStream pipe)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                pipe.Dispose();
                throw new ObjectDisposedException(nameof(NamedPipeTransport));
            }

            _activePipes.Add(pipe);
        }
    }

    private void ReleasePipe(PipeStream pipe)
    {
        lock (_gate)
            _activePipes.Remove(pipe);

        try
        {
            pipe.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or ArgumentException)
        {
        }
    }

    private static string NormalizePipeName(string pipeName)
    {
        if (OperatingSystem.IsWindows())
            return pipeName;

        var tempPath = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var maxPipeNameLength = UnixDomainSocketPathLengthLimit - tempPath.Length - 1 - UnixNamedPipePrefix.Length;
        if (maxPipeNameLength <= 0)
            throw new PlatformNotSupportedException("Current temporary directory leaves no room for Unix named pipe paths.");

        if (pipeName.Length <= maxPipeNameLength)
            return pipeName;

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(pipeName)))
            .ToLowerInvariant();
        var hashLength = Math.Min(hash.Length, maxPipeNameLength);
        var prefixLength = Math.Max(0, maxPipeNameLength - hashLength - 1);
        if (prefixLength == 0)
            return hash[..hashLength];

        return $"{pipeName[..prefixLength]}-{hash[..hashLength]}";
    }
}
