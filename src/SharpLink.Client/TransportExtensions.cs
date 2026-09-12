namespace SharpLink.Client;

/// <summary>Provides built-in client transport configuration extensions.</summary>
public static class TransportExtensions
{
    extension(SharpClientBuilder builder)
    {
        /// <summary>Connects through a local or Windows named pipe.</summary>
        /// <param name="name">The logical pipe name.</param>
        /// <param name="configure">Optional named-pipe options, such as allowing cross-user access.</param>
        public SharpClientBuilder UseNamedPipe(
            string name,
            Action<NamedPipeTransportOptions>? configure = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            var options = new NamedPipeTransportOptions();
            configure?.Invoke(options);
            return builder.UseTransport(new NamedPipeClientTransportFactory(
                name,
                ".",
                options.ToPipeOptions()));
        }

        /// <summary>Connects to a TCP endpoint without TLS.</summary>
        public SharpClientBuilder UseTcp(string ip, int port)
        {
            if (port is < 1 or > IPEndPoint.MaxPort)
                throw new ArgumentOutOfRangeException(nameof(port));

            var endPoint = new IPEndPoint(IPAddress.Parse(ip), port);
            return builder.UseTransport(new SocketClientTransportFactory(endPoint));
        }

        /// <summary>Uses TCP with TLS completed before the SharpLink protocol handshake.</summary>
        /// <param name="ip">The server IP address.</param>
        /// <param name="port">The server TCP port.</param>
        /// <param name="tlsOptions">TLS client authentication settings. Default certificate validation remains enabled when no callback is supplied.</param>
        /// <param name="tlsHandshakeTimeout">Independent positive TLS handshake timeout, up to 2,147,483,647 milliseconds. Defaults to 10 seconds.</param>
        public SharpClientBuilder UseTcp(
            string ip,
            int port,
            SslClientAuthenticationOptions tlsOptions,
            TimeSpan? tlsHandshakeTimeout = null)
        {
            ArgumentNullException.ThrowIfNull(tlsOptions);
            if (port is < 1 or > IPEndPoint.MaxPort)
                throw new ArgumentOutOfRangeException(nameof(port));

            var endPoint = new IPEndPoint(IPAddress.Parse(ip), port);
            return builder.UseTransport(new SocketClientTransportFactory(
                endPoint,
                tlsOptions: tlsOptions,
                tlsHandshakeTimeout: tlsHandshakeTimeout));
        }

        /// <summary>Connects through a Unix-domain socket.</summary>
        public SharpClientBuilder UseUds(string socketPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
            var endPoint = new UnixDomainSocketEndPoint(socketPath);
            return builder.UseTransport(new SocketClientTransportFactory(endPoint));
        }

        /// <summary>Connects through a one-time anonymous-pipe handle pair.</summary>
        /// <remarks>Handle values are secrets and must not be logged or reused.</remarks>
        public SharpClientBuilder UseAnonymousPipe(string inHandle, string outHandle)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(inHandle);
            ArgumentException.ThrowIfNullOrWhiteSpace(outHandle);
            return builder.UseTransport(new AnonymousPipeClientTransportFactory(inHandle, outHandle));
        }

        /// <summary>Uses an explicit same-user, same-machine shared-memory transport.</summary>
        public SharpClientBuilder UseSharedMemory(
            string name,
            Action<SharedMemoryTransportOptions>? configure = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            var options = new SharedMemoryTransportOptions();
            configure?.Invoke(options);
            options.Validate();
            return builder.UseTransport(new SharedMemoryClientTransportFactory(name, options));
        }
    }
}
