
using System.IO;

namespace SharpLink.Server;

public static class TransportExtensions
{
    extension(SharpLinkServerBuilder builder)
    {
        public SharpLinkServerBuilder UseNamedPipe(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return builder.UseTransport(new NamedPipeServerTransportListener(name));
        }

        public SharpLinkServerBuilder UseTcp(int port, string ip = "0.0.0.0", int backlog = 512)
        {
            if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
                throw new ArgumentOutOfRangeException(nameof(port));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backlog);

            var endPoint = new IPEndPoint(IPAddress.Parse(ip), port);
            return builder.UseTransport(new SocketServerTransportListener(endPoint, backlog));
        }

        /// <summary>Uses TCP with TLS completed before the SharpLink protocol handshake.</summary>
        /// <param name="port">The TCP port to bind.</param>
        /// <param name="tlsOptions">TLS server authentication settings.</param>
        /// <param name="ip">The local IP address to bind.</param>
        /// <param name="backlog">The operating-system accept backlog.</param>
        /// <param name="tlsHandshakeTimeout">Independent TLS handshake timeout. Defaults to 10 seconds.</param>
        public SharpLinkServerBuilder UseTcp(
            int port,
            SslServerAuthenticationOptions tlsOptions,
            string ip = "0.0.0.0",
            int backlog = 512,
            TimeSpan? tlsHandshakeTimeout = null)
        {
            ArgumentNullException.ThrowIfNull(tlsOptions);
            if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
                throw new ArgumentOutOfRangeException(nameof(port));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backlog);

            var endPoint = new IPEndPoint(IPAddress.Parse(ip), port);
            return builder.UseTransport(new SocketServerTransportListener(
                endPoint,
                backlog,
                tlsOptions: tlsOptions,
                tlsHandshakeTimeout: tlsHandshakeTimeout));
        }

        public SharpLinkServerBuilder UseUds(string socketPath, int backlog = 512)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backlog);

            var endPoint = new UnixDomainSocketEndPoint(socketPath);
            return builder.UseTransport(new SocketServerTransportListener(endPoint, backlog));
        }

        public SharpLinkServerBuilder UseAnonymousPipe()
        {
            return builder.UseTransport(new AnonymousPipeServerTransportListener());
        }

        /// <summary>Uses an explicit same-user, same-machine shared-memory transport.</summary>
        public SharpLinkServerBuilder UseSharedMemory(
            string name,
            Action<SharedMemoryTransportOptions>? configure = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            var options = new SharedMemoryTransportOptions();
            configure?.Invoke(options);
            options.Validate();
            return builder.UseTransport(new SharedMemoryServerTransportListener(name, options));
        }

    }
}
