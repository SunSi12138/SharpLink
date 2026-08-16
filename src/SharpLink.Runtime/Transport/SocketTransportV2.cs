namespace SharpLink.Runtime;

/// <summary>Configures sockets created by SharpLink transport factories and listeners.</summary>
public sealed class SocketTransportOptions
{
    private static readonly TimeSpan SMaximumKeepAliveDuration =
        TimeSpan.FromSeconds(int.MaxValue);

    /// <summary>Gets or sets whether TCP disables Nagle buffering.</summary>
    public bool NoDelay { get; set; } = true;

    /// <summary>Gets or sets whether TCP keep-alive probes are enabled.</summary>
    public bool KeepAlive { get; set; } = true;

    /// <summary>Gets or sets the idle time before TCP keep-alive probes begin, up to 2,147,483,647 seconds.</summary>
    public TimeSpan KeepAliveTime { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the interval between TCP keep-alive probes, up to 2,147,483,647 seconds.</summary>
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
        ArgumentOutOfRangeException.ThrowIfGreaterThan(KeepAliveTime, SMaximumKeepAliveDuration);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(KeepAliveInterval, SMaximumKeepAliveDuration);
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
    /// <param name="tlsHandshakeTimeout">Independent positive TLS handshake timeout, up to 2,147,483,647 milliseconds. Defaults to 10 seconds.</param>
    public SocketClientTransportFactory(
        EndPoint remoteEndPoint,
        SocketTransportOptions? options = null,
        SslClientAuthenticationOptions? tlsOptions = null,
        TimeSpan? tlsHandshakeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        _remoteEndPoint = SocketTransportSocketFactory.Snapshot(remoteEndPoint);
        if (_remoteEndPoint is IPEndPoint { Port: 0 } or DnsEndPoint { Port: 0 })
            throw new ArgumentOutOfRangeException(nameof(remoteEndPoint), "A client remote endpoint requires a non-zero port.");
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
    private Socket _listener;
    private readonly SocketTransportOptions _options;
    private SslServerAuthenticationOptions? _tlsOptions;
    private TimeSpan _tlsHandshakeTimeout;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly string? _ownedUnixSocketPath;
    private readonly UnixSocketPathIdentity? _ownedUnixSocketIdentity;
    private readonly int _backlog;
    private readonly int _port;
    private int _disposed;

    /// <summary>Creates, binds, and starts a socket listener.</summary>
    /// <param name="localEndPoint">The TCP or Unix-domain endpoint to bind.</param>
    /// <param name="backlog">The operating-system accept backlog.</param>
    /// <param name="options">Optional accepted-socket settings, copied during construction.</param>
    /// <param name="tlsOptions">Optional TLS settings. A null value keeps accepted sockets plaintext.</param>
    /// <param name="tlsHandshakeTimeout">Independent positive TLS handshake timeout, up to 2,147,483,647 milliseconds. Defaults to 10 seconds.</param>
    public SocketServerTransportListener(
        EndPoint localEndPoint,
        int backlog = 512,
        SocketTransportOptions? options = null,
        SslServerAuthenticationOptions? tlsOptions = null,
        TimeSpan? tlsHandshakeTimeout = null)
        : this(localEndPoint, backlog, options, tlsOptions, tlsHandshakeTimeout, null)
    {
    }

    internal SocketServerTransportListener(
        EndPoint localEndPoint,
        int backlog,
        SocketTransportOptions? options,
        SslServerAuthenticationOptions? tlsOptions,
        TimeSpan? tlsHandshakeTimeout,
        Action<string, UnixSocketPathIdentity>? permissionHardeningOverride)
    {
        ArgumentNullException.ThrowIfNull(localEndPoint);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backlog);
        _backlog = backlog;
        _port = localEndPoint is IPEndPoint ipEndPoint ? ipEndPoint.Port : 0;
        _options = (options ?? new SocketTransportOptions()).CloneValidated();
        _tlsOptions = TlsAuthenticationOptionsSnapshot.Clone(tlsOptions);
        _tlsHandshakeTimeout = TlsAuthenticationOptionsSnapshot.ValidateTimeout(tlsHandshakeTimeout);
        _listener = SocketTransportSocketFactory.Create(localEndPoint);

        string? unixPath = null;
        string? boundUnixPath = null;
        UnixSocketPathIdentity? boundUnixIdentity = null;
        try
        {
            if (localEndPoint is UnixDomainSocketEndPoint uds)
            {
                unixPath = SocketTransportSocketFactory.GetFileSystemPath(uds);
                if (File.Exists(unixPath))
                {
                    throw new IOException(
                        $"Unix-domain socket path '{unixPath}' already exists and will not be replaced.");
                }
            }

            _listener.Bind(localEndPoint);
            boundUnixPath = unixPath;
            if (unixPath is not null && !OperatingSystem.IsWindows())
            {
                // UnixSocketPathIdentity.Capture returns null only on Windows, which
                // is excluded above; on every other platform it either returns the
                // identity or throws. The coalescing throw only satisfies nullable
                // flow analysis and doubles as a fail-closed invariant.
                var identity = UnixSocketPathIdentity.Capture(unixPath)
                    ?? throw new UnauthorizedAccessException(
                        "The Unix-domain socket path could not be identified to secure its permissions.");
                boundUnixIdentity = identity;
                HardenUnixSocketPermissions(
                    unixPath,
                    identity,
                    permissionHardeningOverride);
            }
            _listener.Listen(_backlog);
            LocalEndPoint = _listener.LocalEndPoint;
            _ownedUnixSocketPath = unixPath;
            _ownedUnixSocketIdentity = boundUnixIdentity;
        }
        catch
        {
            DisposeListenerPreservingPathReplacement(
                _listener,
                boundUnixPath,
                boundUnixIdentity);
            TryDeleteOwnedUnixSocketPath(boundUnixPath, boundUnixIdentity);
            throw;
        }
    }

    /// <inheritdoc />
    public EndPoint? LocalEndPoint { get; private set; }

    internal bool UsesTls =>
        _tlsOptions is not null &&
        _tlsOptions.EncryptionPolicy == EncryptionPolicy.RequireEncryption;

    internal void ConfigureListenAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (LocalEndPoint is not IPEndPoint)
            throw new InvalidOperationException("The listen address can only be changed for TCP listeners.");

        var replacementEndPoint = new IPEndPoint(address, _port);

        if (_port == 0)
        {
            var replacement = CreateBoundTcpListener(replacementEndPoint);
            var previous = _listener;
            _listener = replacement;
            LocalEndPoint = replacement.LocalEndPoint;
            previous.Dispose();
            return;
        }

        var original = _listener;
        var replacementSocket = SocketTransportSocketFactory.Create(replacementEndPoint);
        try
        {
            original.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress,
                true);
            replacementSocket.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress,
                true);
            replacementSocket.Bind(replacementEndPoint);
            replacementSocket.Listen(_backlog);
        }
        catch
        {
            replacementSocket.Dispose();
            throw;
        }

        _listener = replacementSocket;
        LocalEndPoint = replacementSocket.LocalEndPoint;
        original.Dispose();
    }

    private Socket CreateBoundTcpListener(IPEndPoint endPoint)
    {
        var listener = SocketTransportSocketFactory.Create(endPoint);
        try
        {
            listener.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress,
                true);
            listener.Bind(endPoint);
            listener.Listen(_backlog);
            return listener;
        }
        catch
        {
            listener.Dispose();
            throw;
        }
    }

    internal void ConfigureTls(
        SslServerAuthenticationOptions tlsOptions,
        TimeSpan? tlsHandshakeTimeout)
    {
        ArgumentNullException.ThrowIfNull(tlsOptions);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (LocalEndPoint is not IPEndPoint)
            throw new InvalidOperationException("TLS can only be configured for TCP listeners.");

        _tlsOptions = TlsAuthenticationOptionsSnapshot.Clone(tlsOptions);
        _tlsHandshakeTimeout = TlsAuthenticationOptionsSnapshot.ValidateTimeout(tlsHandshakeTimeout);
    }

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
        DisposeListenerPreservingPathReplacement(
            _listener,
            _ownedUnixSocketPath,
            _ownedUnixSocketIdentity);
        _disposeCts.Dispose();
        TryDeleteOwnedUnixSocketPath(_ownedUnixSocketPath, _ownedUnixSocketIdentity);

        return ValueTask.CompletedTask;
    }

    private static void DisposeListenerPreservingPathReplacement(
        Socket listener,
        string? path,
        UnixSocketPathIdentity? identity)
    {
        var preservation = UnixSocketPathIdentity.PreserveReplacement(path, identity);
        try
        {
            listener.Dispose();
        }
        finally
        {
            preservation?.Restore();
        }
    }

    private static void TryDeleteOwnedUnixSocketPath(
        string? path,
        UnixSocketPathIdentity? identity)
    {
        if (path is not null)
        {
            try
            {
                if (!OperatingSystem.IsWindows() &&
                    (identity is null || !identity.Value.Matches(path)))
                {
                    return;
                }
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"SharpLink could not remove Unix-domain socket path '{path}': {ex.Message}");
            }
        }
    }

    private const UnixFileMode DefaultUnixSocketMode =
        UnixFileMode.UserRead |
        UnixFileMode.UserWrite;

    private const UnixFileMode DisallowedUnixSocketMode =
        UnixFileMode.GroupRead |
        UnixFileMode.GroupWrite |
        UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead |
        UnixFileMode.OtherWrite |
        UnixFileMode.OtherExecute;

    /// <summary>
    /// Restricts a SharpLink-created filesystem Unix-domain socket to the current user
    /// before the listener accepts connections. Any failure throws so the constructor
    /// fails closed and the existing cleanup path removes the owned socket node.
    /// </summary>
    internal static void HardenUnixSocketPermissions(
        string path,
        UnixSocketPathIdentity identity,
        Action<string, UnixSocketPathIdentity>? overrideForTesting = null)
    {
        if (OperatingSystem.IsWindows())
            return;

        if (overrideForTesting is not null)
        {
            overrideForTesting(path, identity);
            return;
        }

        if (!identity.Matches(path))
        {
            throw new UnauthorizedAccessException(
                "The Unix-domain socket path changed before its permissions could be secured.");
        }

        File.SetUnixFileMode(path, DefaultUnixSocketMode);

        if (!identity.Matches(path))
        {
            throw new UnauthorizedAccessException(
                "The Unix-domain socket path changed while its permissions were being secured.");
        }

        var actual = File.GetUnixFileMode(path);
        if ((actual & DisallowedUnixSocketMode) != 0 ||
            (actual & DefaultUnixSocketMode) != DefaultUnixSocketMode)
        {
            throw new UnauthorizedAccessException(
                "The Unix-domain socket permissions could not be restricted to the current user.");
        }
    }
}

internal static class SocketTransportSocketFactory
{
    internal static EndPoint Snapshot(EndPoint endPoint)
        => endPoint switch
        {
            IPEndPoint ip => new IPEndPoint(CloneAddress(ip.Address), ip.Port),
            DnsEndPoint dns => new DnsEndPoint(dns.Host, dns.Port, dns.AddressFamily),
            UnixDomainSocketEndPoint unix => unix.Create(unix.Serialize()),
            _ => SnapshotCustom(endPoint)
        };

    private static EndPoint SnapshotCustom(EndPoint endPoint)
    {
        try
        {
            var snapshot = endPoint.Create(endPoint.Serialize());
            if (ReferenceEquals(snapshot, endPoint))
            {
                throw new InvalidOperationException(
                    "The endpoint Create method returned its mutable source instance.");
            }
            return snapshot;
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException)
        {
            throw new ArgumentException(
                "Custom socket endpoints must support an independent Create(Serialize()) snapshot.",
                nameof(endPoint),
                exception);
        }
    }

    internal static string? GetFileSystemPath(UnixDomainSocketEndPoint endPoint)
    {
        var address = endPoint.Serialize();
        return address.Size > 2 && address[2] == 0 ? null : endPoint.ToString();
    }

    private static IPAddress CloneAddress(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPAddress(address.GetAddressBytes(), address.ScopeId)
            : new IPAddress(address.GetAddressBytes());

    public static Socket Create(EndPoint endPoint)
    {
        if (endPoint is DnsEndPoint)
        {
            if (Socket.OSSupportsIPv6)
            {
                Socket? dualMode = null;
                try
                {
                    dualMode = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp)
                    {
                        DualMode = true
                    };
                    return dualMode;
                }
                catch (Exception exception) when (exception is SocketException or NotSupportedException)
                {
                    dualMode?.Dispose();
                }
            }

            return new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
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

internal readonly record struct UnixSocketPathIdentity(long Device, long Inode)
{
    private const int FileTypeMask = 0xF000;
    private const int DirectoryFileType = 0x4000;
    private const int SocketFileType = 0xC000;

    internal static UnixSocketPathIdentity? Capture(string path)
    {
        if (OperatingSystem.IsWindows())
            return null;
        if (LStat(path, out var status) != 0)
        {
            throw new IOException(
                $"Could not identify Unix-domain socket path '{path}'.",
                new System.ComponentModel.Win32Exception(
                    System.Runtime.InteropServices.Marshal.GetLastPInvokeError()));
        }
        if ((status.Mode & FileTypeMask) != SocketFileType)
            throw new IOException($"Unix-domain socket path '{path}' is not a socket node.");
        return new UnixSocketPathIdentity(status.Device, status.Inode);
    }

    internal bool Matches(string path)
        => LStat(path, out var status) == 0 &&
           (status.Mode & FileTypeMask) == SocketFileType &&
           status.Device == Device &&
           status.Inode == Inode;

    internal static UnixSocketPathPreservation? PreserveReplacement(
        string? path,
        UnixSocketPathIdentity? identity)
    {
        if (OperatingSystem.IsWindows() || path is null ||
            identity is { } captured && captured.Matches(path) ||
            LStat(path, out var status) != 0)
        {
            return null;
        }

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            directory = Directory.GetCurrentDirectory();
        var backupPath = Path.Combine(
            directory,
            $".sharplink-preserve-{Guid.NewGuid():N}");
        var isDirectory = (status.Mode & FileTypeMask) == DirectoryFileType;
        if (isDirectory)
            Directory.Move(path, backupPath);
        else
            File.Move(path, backupPath);
        return new UnixSocketPathPreservation(path, backupPath, isDirectory);
    }

    internal static bool PathExists(string path) => LStat(path, out _) == 0;

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(
        System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport(
        "System.Native",
        EntryPoint = "SystemNative_LStat",
        SetLastError = true)]
    private static extern int LStat(
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)]
        string path,
        out UnixFileStatus status);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct UnixFileStatus
    {
        internal int Flags;
        internal int Mode;
        internal uint UserId;
        internal uint GroupId;
        internal long Size;
        internal long AccessTime;
        internal long AccessTimeNanoseconds;
        internal long ModificationTime;
        internal long ModificationTimeNanoseconds;
        internal long ChangeTime;
        internal long ChangeTimeNanoseconds;
        internal long BirthTime;
        internal long BirthTimeNanoseconds;
        internal long Device;
        internal long RawDevice;
        internal long Inode;
        internal uint UserFlags;
        internal int HardLinkCount;
    }
}

internal sealed class UnixSocketPathPreservation(
    string path,
    string backupPath,
    bool isDirectory)
{
    private int _restored;

    internal void Restore()
    {
        if (Interlocked.Exchange(ref _restored, 1) != 0)
            return;
        if (UnixSocketPathIdentity.PathExists(path))
        {
            throw new IOException(
                $"Unix-domain socket path replacement could not be restored because '{path}' was recreated; " +
                $"the preserved entry remains at '{backupPath}'.");
        }
        if (isDirectory)
            Directory.Move(backupPath, path);
        else
            File.Move(backupPath, path);
    }
}
