namespace SharpLink.Runtime;

internal interface ITransportSecurityHandshake
{
    ValueTask AuthenticateAsync(CancellationToken cancellationToken);
}

internal interface ITransportSecurityInfo
{
    SslProtocols Protocol { get; }
    TlsCipherSuite CipherSuite { get; }
}

internal sealed class TlsStreamTransportConnection : StreamTransportConnection,
    ITransportSecurityHandshake,
    ITransportSecurityInfo
{
    private readonly SslStream _stream;
    private readonly SslClientAuthenticationOptions? _clientOptions;
    private readonly SslServerAuthenticationOptions? _serverOptions;
    private readonly TimeSpan _handshakeTimeout;
    private readonly Lock _authenticationGate = new();
    private Task? _authenticationTask;

    public TlsStreamTransportConnection(
        Stream innerStream,
        SslClientAuthenticationOptions options,
        TimeSpan handshakeTimeout,
        EndPoint? localEndPoint,
        EndPoint? remoteEndPoint)
        : this(new SslStream(innerStream, leaveInnerStreamOpen: false), options, null, handshakeTimeout,
            localEndPoint, remoteEndPoint)
    {
    }

    public TlsStreamTransportConnection(
        Stream innerStream,
        SslServerAuthenticationOptions options,
        TimeSpan handshakeTimeout,
        EndPoint? localEndPoint,
        EndPoint? remoteEndPoint)
        : this(new SslStream(innerStream, leaveInnerStreamOpen: false), null, options, handshakeTimeout,
            localEndPoint, remoteEndPoint)
    {
    }

    private TlsStreamTransportConnection(
        SslStream stream,
        SslClientAuthenticationOptions? clientOptions,
        SslServerAuthenticationOptions? serverOptions,
        TimeSpan handshakeTimeout,
        EndPoint? localEndPoint,
        EndPoint? remoteEndPoint)
        : base(stream, localEndPoint, remoteEndPoint)
    {
        _stream = stream;
        _clientOptions = clientOptions;
        _serverOptions = serverOptions;
        _handshakeTimeout = handshakeTimeout;
    }

    public SslProtocols Protocol => _stream.SslProtocol;
    public TlsCipherSuite CipherSuite => _stream.NegotiatedCipherSuite;

    public ValueTask AuthenticateAsync(CancellationToken cancellationToken)
    {
        Task task;
        lock (_authenticationGate)
            task = _authenticationTask ??= AuthenticateCoreAsync(cancellationToken);
        return new ValueTask(task);
    }

    private async Task AuthenticateCoreAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(_handshakeTimeout);
        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);
        try
        {
            if (_clientOptions is not null)
            {
                await _stream.AuthenticateAsClientAsync(_clientOptions, handshakeCts.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                await _stream.AuthenticateAsServerAsync(_serverOptions!, handshakeCts.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception) when (
            timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.Unavailable,
                $"TLS handshake timed out after {_handshakeTimeout}.",
                exception);
        }
    }
}

internal static class TlsAuthenticationOptionsSnapshot
{
    private static readonly TimeSpan SDefaultHandshakeTimeout = TimeSpan.FromSeconds(10);

    public static TimeSpan ValidateTimeout(TimeSpan? timeout)
    {
        var value = timeout ?? SDefaultHandshakeTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
        return value;
    }

    public static SslClientAuthenticationOptions? Clone(
        SslClientAuthenticationOptions? source,
        string? defaultTargetHost = null)
    {
        if (source is null)
            return null;
        var targetHost = string.IsNullOrWhiteSpace(source.TargetHost) ? defaultTargetHost : source.TargetHost;
        ArgumentException.ThrowIfNullOrWhiteSpace(targetHost);
        var clone = new SslClientAuthenticationOptions
        {
            TargetHost = targetHost,
            ClientCertificates = source.ClientCertificates is null
                ? null
                : new X509CertificateCollection(source.ClientCertificates),
            ClientCertificateContext = source.ClientCertificateContext,
            EnabledSslProtocols = source.EnabledSslProtocols,
            CertificateRevocationCheckMode = source.CertificateRevocationCheckMode,
            EncryptionPolicy = source.EncryptionPolicy,
            RemoteCertificateValidationCallback = source.RemoteCertificateValidationCallback,
            LocalCertificateSelectionCallback = source.LocalCertificateSelectionCallback,
            ApplicationProtocols = source.ApplicationProtocols is null
                ? null
                : new List<SslApplicationProtocol>(source.ApplicationProtocols),
            AllowRenegotiation = source.AllowRenegotiation,
            AllowTlsResume = source.AllowTlsResume,
            CipherSuitesPolicy = source.CipherSuitesPolicy,
            CertificateChainPolicy = source.CertificateChainPolicy
        };
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
        {
            clone.AllowRsaPkcs1Padding = source.AllowRsaPkcs1Padding;
            clone.AllowRsaPssPadding = source.AllowRsaPssPadding;
        }
        return clone;
    }

    public static SslServerAuthenticationOptions? Clone(SslServerAuthenticationOptions? source)
    {
        if (source is null)
            return null;
        if (source.ServerCertificate is null && source.ServerCertificateContext is null &&
            source.ServerCertificateSelectionCallback is null)
        {
            throw new ArgumentException(
                "TLS server options require a certificate, certificate context, or selection callback.",
                nameof(source));
        }

        return new SslServerAuthenticationOptions
        {
            ServerCertificate = source.ServerCertificate,
            ServerCertificateContext = source.ServerCertificateContext,
            ServerCertificateSelectionCallback = source.ServerCertificateSelectionCallback,
            ClientCertificateRequired = source.ClientCertificateRequired,
            EnabledSslProtocols = source.EnabledSslProtocols,
            CertificateRevocationCheckMode = source.CertificateRevocationCheckMode,
            EncryptionPolicy = source.EncryptionPolicy,
            RemoteCertificateValidationCallback = source.RemoteCertificateValidationCallback,
            ApplicationProtocols = source.ApplicationProtocols is null
                ? null
                : new List<SslApplicationProtocol>(source.ApplicationProtocols),
            AllowRenegotiation = source.AllowRenegotiation,
            AllowTlsResume = source.AllowTlsResume,
            CipherSuitesPolicy = source.CipherSuitesPolicy
        };
    }
}
