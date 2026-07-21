namespace SharpLink.Client;

/// <summary>Creates built-in endpoint transport factories for static SharpLink topologies.</summary>
public static class SharpLinkTransportFactories
{
    /// <summary>Creates a factory for TCP and Unix-domain socket endpoint addresses.</summary>
    /// <param name="options">Optional socket settings copied by each created transport factory.</param>
    /// <returns>An endpoint factory that accepts <see cref="SharpLinkTcpAddress"/> and <see cref="SharpLinkUnixDomainSocketAddress"/>.</returns>
    public static SharpLinkEndpointTransportFactory Sockets(SocketTransportOptions? options = null)
        => endpoint => endpoint.Address switch
        {
            SharpLinkTcpAddress tcp => new SocketClientTransportFactory(CreateTcpEndPoint(tcp), options),
            SharpLinkUnixDomainSocketAddress uds => new SocketClientTransportFactory(new UnixDomainSocketEndPoint(uds.Path), options),
            _ => throw new ArgumentException("Sockets require a TCP or Unix-domain socket endpoint address.", nameof(endpoint))
        };

    /// <summary>Creates a TLS socket factory for TCP and Unix-domain socket endpoint addresses.</summary>
    /// <param name="tlsOptions">TLS settings copied for every endpoint factory.</param>
    /// <param name="options">Optional socket settings copied by each created transport factory.</param>
    /// <param name="tlsHandshakeTimeout">An optional positive TLS handshake timeout.</param>
    /// <remarks>
    /// When the supplied TLS options omit <see cref="SslClientAuthenticationOptions.TargetHost"/>,
    /// the endpoint Authority is used; TCP endpoints then fall back to their Host.
    /// </remarks>
    public static SharpLinkEndpointTransportFactory Sockets(
        SslClientAuthenticationOptions tlsOptions,
        SocketTransportOptions? options = null,
        TimeSpan? tlsHandshakeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(tlsOptions);
        return endpoint => endpoint.Address switch
        {
            SharpLinkTcpAddress tcp => new SocketClientTransportFactory(
                CreateTcpEndPoint(tcp), options, CreateTlsOptions(tlsOptions, endpoint.Authority ?? tcp.Host), tlsHandshakeTimeout),
            SharpLinkUnixDomainSocketAddress uds => new SocketClientTransportFactory(
                new UnixDomainSocketEndPoint(uds.Path), options, CreateTlsOptions(tlsOptions, endpoint.Authority), tlsHandshakeTimeout),
            _ => throw new ArgumentException("Sockets require a TCP or Unix-domain socket endpoint address.", nameof(endpoint))
        };
    }

    /// <summary>Creates a factory for named-pipe endpoint addresses.</summary>
    /// <returns>An endpoint factory that accepts <see cref="SharpLinkNamedPipeAddress"/>.</returns>
    public static SharpLinkEndpointTransportFactory NamedPipes()
        => endpoint => endpoint.Address is SharpLinkNamedPipeAddress pipe
            ? new NamedPipeClientTransportFactory(pipe.PipeName, pipe.ServerName)
            : throw new ArgumentException("Named pipes require a named-pipe endpoint address.", nameof(endpoint));

    /// <summary>Creates a factory for shared-memory endpoint addresses.</summary>
    /// <param name="configure">Optionally configures options copied by each created factory.</param>
    /// <returns>An endpoint factory that accepts <see cref="SharpLinkSharedMemoryAddress"/>.</returns>
    public static SharpLinkEndpointTransportFactory SharedMemory(Action<SharedMemoryTransportOptions>? configure = null)
    {
        var options = new SharedMemoryTransportOptions();
        configure?.Invoke(options);
        options.Validate();
        return endpoint => endpoint.Address is SharpLinkSharedMemoryAddress memory
            ? new SharedMemoryClientTransportFactory(memory.Name, options)
            : throw new ArgumentException("Shared memory requires a shared-memory endpoint address.", nameof(endpoint));
    }

    private static EndPoint CreateTcpEndPoint(SharpLinkTcpAddress address)
        => IPAddress.TryParse(address.Host, out var ipAddress)
            ? new IPEndPoint(ipAddress, address.Port)
            : new DnsEndPoint(address.Host, address.Port);

    private static SslClientAuthenticationOptions CreateTlsOptions(
        SslClientAuthenticationOptions source,
        string? defaultTargetHost)
    {
        var targetHost = string.IsNullOrWhiteSpace(source.TargetHost) ? defaultTargetHost : source.TargetHost;
        ArgumentException.ThrowIfNullOrWhiteSpace(targetHost);
        return new SslClientAuthenticationOptions
        {
            TargetHost = targetHost,
            ClientCertificates = source.ClientCertificates is null
                ? null
                : new System.Security.Cryptography.X509Certificates.X509CertificateCollection(source.ClientCertificates),
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
    }
}
