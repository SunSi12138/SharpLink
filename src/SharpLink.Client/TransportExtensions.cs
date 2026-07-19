namespace SharpLink.Client;

public static class TransportExtensions
{ 
    extension(SharpClientBuilder builder)
    {
        public SharpClientBuilder UseNamedPipe(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return builder.UseTransport(new NamedPipeClientTransportFactory(name));
        }

        public SharpClientBuilder UseTcp(string ip, int port)
        {
            if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
                throw new ArgumentOutOfRangeException(nameof(port));

            var endPoint = new IPEndPoint(IPAddress.Parse(ip), port);
            return builder.UseTransport(new SocketClientTransportFactory(endPoint));
        }

        /// <summary>Uses TCP with TLS completed before the SharpLink protocol handshake.</summary>
        /// <param name="ip">The server IP address.</param>
        /// <param name="port">The server TCP port.</param>
        /// <param name="tlsOptions">TLS client authentication settings. Default certificate validation remains enabled when no callback is supplied.</param>
        /// <param name="tlsHandshakeTimeout">Independent TLS handshake timeout. Defaults to 10 seconds.</param>
        public SharpClientBuilder UseTcp(
            string ip,
            int port,
            SslClientAuthenticationOptions tlsOptions,
            TimeSpan? tlsHandshakeTimeout = null)
        {
            ArgumentNullException.ThrowIfNull(tlsOptions);
            if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
                throw new ArgumentOutOfRangeException(nameof(port));

            var endPoint = new IPEndPoint(IPAddress.Parse(ip), port);
            return builder.UseTransport(new SocketClientTransportFactory(
                endPoint,
                tlsOptions: tlsOptions,
                tlsHandshakeTimeout: tlsHandshakeTimeout));
        }

        public SharpClientBuilder UseUds(string socketPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
            var endPoint = new UnixDomainSocketEndPoint(socketPath);
            return builder.UseTransport(new SocketClientTransportFactory(endPoint));
        }

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
