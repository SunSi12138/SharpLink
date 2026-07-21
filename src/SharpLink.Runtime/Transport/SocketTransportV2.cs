namespace SharpLink.Runtime;

/// <summary>Configures sockets created by SharpLink transport factories and listeners.</summary>
public sealed class SocketTransportOptions
{
    /// <summary>Gets or sets whether TCP disables Nagle buffering.</summary>
    public bool NoDelay { get; set; } = true;

    /// <summary>Gets or sets whether TCP keep-alive probes are enabled.</summary>
    public bool KeepAlive { get; set; } = true;

    /// <summary>Gets or sets the idle time before TCP keep-alive probes begin.</summary>
    public TimeSpan KeepAliveTime { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the interval between TCP keep-alive probes.</summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the number of failed TCP keep-alive probes before disconnect.</summary>
    public int KeepAliveRetryCount { get; set; } = 3;

    /// <summary>Gets or sets an optional operating-system send buffer size.</summary>
    public int? SendBufferBytes { get; set; }

    /// <summary>Gets or sets an optional operating-system receive buffer size.</summary>
    public int? ReceiveBufferBytes { get; set; }

    internal SocketTransportOptions CloneValidated()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(KeepAliveTime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(KeepAliveInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(KeepAliveRetryCount);
        if (SendBufferBytes is { } sendBytes)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sendBytes);
        if (ReceiveBufferBytes is { } receiveBytes)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(receiveBytes);

        return new SocketTransportOptions
        {
            NoDelay = NoDelay,
            KeepAlive = KeepAlive,
            KeepAliveTime = KeepAliveTime,
            KeepAliveInterval = KeepAliveInterval,
            KeepAliveRetryCount = KeepAliveRetryCount,
            SendBufferBytes = SendBufferBytes,
            ReceiveBufferBytes = ReceiveBufferBytes
        };
    }
}

/// <summary>Creates a fresh TCP or Unix-domain socket for every connection attempt.</summary>
public sealed class SocketClientTransportFactory : IClientTransportFactory
{
    private readonly EndPoint _remoteEndPoint;
    private readonly SocketTransportOptions _options;
    private readonly SslClientAuthenticationOptions? _tlsOptions;
    private readonly TimeSpan _tlsHandshakeTimeout;
    private readonly CancellationTokenSource _disposeCts = new();
    private int _disposed;

    /// <summary>Creates a socket client factory.</summary>
    /// <param name="remoteEndPoint">The TCP or Unix-domain endpoint to connect.</param>
    /// <param name="options">Optional socket settings, copied during construction.</param>
    /// <param name="tlsOptions">Optional TLS settings. A null value keeps the socket plaintext.</param>
    /// <param name="tlsHandshakeTimeout">Independent TLS handshake timeout. Defaults to 10 seconds.</param>
    public SocketClientTransportFactory(
        EndPoint remoteEndPoint,
        SocketTransportOptions? options = null,
        SslClientAuthenticationOptions? tlsOptions = null,
        TimeSpan? tlsHandshakeTimeout = null)
    {
        _remoteEndPoint = remoteEndPoint ?? throw new ArgumentNullException(nameof(remoteEndPoint));
        _options = (options ?? new SocketTransportOptions()).CloneValidated();
        _tlsOptions = TlsAuthenticationOptionsSnapshot.Clone(tlsOptions);
        _tlsHandshakeTimeout = TlsAuthenticationOptionsSnapshot.ValidateTimeout(tlsHandshakeTimeout);
    }

    /// <inheritdoc />
    public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        var socket = SocketTransportSocketFactory.Create(_remoteEndPoint);
        try
        {
            SocketTransportSocketFactory.ApplyOptions(socket, _options);
            await socket.ConnectAsync(_remoteEndPoint, connectCts.Token).ConfigureAwait(false);
            var stream = new NetworkStream(socket, ownsSocket: true);
            if (_tlsOptions is null)
                return new StreamTransportConnection(stream, socket.LocalEndPoint, socket.RemoteEndPoint);

            var connection = new TlsStreamTransportConnection(
                stream,
                _tlsOptions,
                _tlsHandshakeTimeout,
                socket.LocalEndPoint,
                socket.RemoteEndPoint);
            try
            {
                await connection.AuthenticateAsync(connectCts.Token).ConfigureAwait(false);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            socket.Dispose();
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

/// <summary>Listens for TCP or Unix-domain socket connections.</summary>
public sealed class SocketServerTransportListener : IServerTransportListener
{
    private readonly Socket _listener;
    private readonly SocketTransportOptions _options;
    private readonly SslServerAuthenticationOptions? _tlsOptions;
    private readonly TimeSpan _tlsHandshakeTimeout;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly string? _ownedUnixSocketPath;
    private int _disposed;

    /// <summary>Creates, binds, and starts a socket listener.</summary>
    /// <param name="localEndPoint">The TCP or Unix-domain endpoint to bind.</param>
    /// <param name="backlog">The operating-system accept backlog.</param>
    /// <param name="options">Optional accepted-socket settings, copied during construction.</param>
    /// <param name="tlsOptions">Optional TLS settings. A null value keeps accepted sockets plaintext.</param>
    /// <param name="tlsHandshakeTimeout">Independent TLS handshake timeout. Defaults to 10 seconds.</param>
    public SocketServerTransportListener(
        EndPoint localEndPoint,
        int backlog = 512,
        SocketTransportOptions? options = null,
        SslServerAuthenticationOptions? tlsOptions = null,
        TimeSpan? tlsHandshakeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(localEndPoint);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backlog);
        _options = (options ?? new SocketTransportOptions()).CloneValidated();
        _tlsOptions = TlsAuthenticationOptionsSnapshot.Clone(tlsOptions);
        _tlsHandshakeTimeout = TlsAuthenticationOptionsSnapshot.ValidateTimeout(tlsHandshakeTimeout);
        _listener = SocketTransportSocketFactory.Create(localEndPoint);

        string? unixPath = null;
        try
        {
            if (localEndPoint is UnixDomainSocketEndPoint uds)
            {
                unixPath = uds.ToString();
                if (File.Exists(unixPath))
                    File.Delete(unixPath);
            }

            _listener.Bind(localEndPoint);
            _listener.Listen(backlog);
            LocalEndPoint = _listener.LocalEndPoint;
            _ownedUnixSocketPath = unixPath;
        }
        catch
        {
            _listener.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public EndPoint? LocalEndPoint { get; }

    /// <inheritdoc />
    public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        var socket = await _listener.AcceptAsync(acceptCts.Token).ConfigureAwait(false);
        try
        {
            SocketTransportSocketFactory.ApplyOptions(socket, _options);
            var stream = new NetworkStream(socket, ownsSocket: true);
            return _tlsOptions is null
                ? new StreamTransportConnection(stream, socket.LocalEndPoint, socket.RemoteEndPoint)
                : new TlsStreamTransportConnection(
                    stream,
                    _tlsOptions,
                    _tlsHandshakeTimeout,
                    socket.LocalEndPoint,
                    socket.RemoteEndPoint);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        _disposeCts.Cancel();
        _listener.Dispose();
        _disposeCts.Dispose();
        if (_ownedUnixSocketPath is not null)
        {
            try
            {
                if (File.Exists(_ownedUnixSocketPath))
                    File.Delete(_ownedUnixSocketPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"SharpLink could not remove Unix-domain socket path '{_ownedUnixSocketPath}': {ex.Message}");
            }
        }

        return ValueTask.CompletedTask;
    }
}

internal static class SocketTransportSocketFactory
{
    public static Socket Create(EndPoint endPoint)
    {
        if (endPoint is DnsEndPoint)
        {
            var dualMode = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp)
            {
                DualMode = true
            };
            return dualMode;
        }
        var addressFamily = endPoint.AddressFamily == AddressFamily.Unspecified
            ? AddressFamily.InterNetwork
            : endPoint.AddressFamily;
        var protocol = addressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6
            ? ProtocolType.Tcp
            : ProtocolType.Unspecified;
        return new Socket(addressFamily, SocketType.Stream, protocol);
    }

    public static void ApplyOptions(Socket socket, SocketTransportOptions options)
    {
        if (socket.ProtocolType == ProtocolType.Tcp)
        {
            socket.NoDelay = options.NoDelay;
            if (options.KeepAlive)
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                TryApplyKeepAliveDetail(socket, SocketOptionName.TcpKeepAliveTime, options.KeepAliveTime);
                TryApplyKeepAliveDetail(socket, SocketOptionName.TcpKeepAliveInterval, options.KeepAliveInterval);
                TryApplyKeepAliveDetail(socket, SocketOptionName.TcpKeepAliveRetryCount, options.KeepAliveRetryCount);
            }
        }

        if (options.SendBufferBytes is { } sendBytes)
            socket.SendBufferSize = sendBytes;
        if (options.ReceiveBufferBytes is { } receiveBytes)
            socket.ReceiveBufferSize = receiveBytes;
    }

    private static void TryApplyKeepAliveDetail(Socket socket, SocketOptionName option, TimeSpan value)
        => TryApplyKeepAliveDetail(socket, option, checked((int)Math.Ceiling(value.TotalSeconds)));

    private static void TryApplyKeepAliveDetail(Socket socket, SocketOptionName option, int value)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Tcp, option, value);
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException or NotSupportedException)
        {
            Debug.WriteLine($"SharpLink skipped unsupported socket option {option}: {ex.Message}");
        }
    }
}
