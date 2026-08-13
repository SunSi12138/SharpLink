
using System.IO;

namespace SharpLink.Server;

/// <summary>Provides built-in server transport configuration extensions.</summary>
public static class TransportExtensions
{
    extension(SharpLinkServerBuilder builder)
    {
        /// <summary>Listens on a local or Windows named pipe.</summary>
        public SharpLinkServerBuilder UseNamedPipe(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return builder.UseTransport(new NamedPipeServerTransportListener(name));
        }

        /// <summary>Listens on the loopback TCP endpoint without TLS.</summary>
        public SharpLinkServerBuilder UseTcp(int port, int backlog = 512)
        {
            ValidateTcpPort(port);
            ValidateBacklog(backlog);

            var endPoint = new IPEndPoint(IPAddress.Loopback, port);
            return builder.UseTransport(new SocketServerTransportListener(endPoint, backlog));
        }

        /// <summary>Listens on a TCP endpoint without TLS.</summary>
        public SharpLinkServerBuilder UseTcp(int port, IPAddress address, int backlog = 512)
        {
            ArgumentNullException.ThrowIfNull(address);
            ValidateTcpPort(port);
            ValidateBacklog(backlog);

            var endPoint = new IPEndPoint(address, port);
            return builder.UseTransport(new SocketServerTransportListener(endPoint, backlog));
        }

        /// <summary>Legacy TCP overload that binds to a parsed IP address without TLS.</summary>
        /// <remarks>Prefer the typed IPAddress overload or <c>UseTcp(port).ListenOn(address)</c>.</remarks>
        public SharpLinkServerBuilder UseTcp(int port, string ip, int backlog = 512)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(ip);
            ValidateTcpPort(port);
            ValidateBacklog(backlog);

            var endPoint = new IPEndPoint(IPAddress.Parse(ip), port);
            return builder.UseTransport(new SocketServerTransportListener(endPoint, backlog));
        }

        /// <summary>Uses loopback TCP with TLS completed before the SharpLink protocol handshake.</summary>
        public SharpLinkServerBuilder UseTcp(
            int port,
            SslServerAuthenticationOptions tlsOptions,
            int backlog = 512,
            TimeSpan? tlsHandshakeTimeout = null)
        {
            ArgumentNullException.ThrowIfNull(tlsOptions);
            ValidateTcpPort(port);
            ValidateBacklog(backlog);

            var endPoint = new IPEndPoint(IPAddress.Loopback, port);
            return builder.UseTransport(new SocketServerTransportListener(
                endPoint,
                backlog,
                tlsOptions: tlsOptions,
                tlsHandshakeTimeout: tlsHandshakeTimeout));
        }

        /// <summary>Uses TCP with TLS completed before the SharpLink protocol handshake.</summary>
        public SharpLinkServerBuilder UseTcp(
            int port,
            SslServerAuthenticationOptions tlsOptions,
            IPAddress address,
            int backlog = 512,
            TimeSpan? tlsHandshakeTimeout = null)
        {
            ArgumentNullException.ThrowIfNull(tlsOptions);
            ArgumentNullException.ThrowIfNull(address);
            ValidateTcpPort(port);
            ValidateBacklog(backlog);

            var endPoint = new IPEndPoint(address, port);
            return builder.UseTransport(new SocketServerTransportListener(
                endPoint,
                backlog,
                tlsOptions: tlsOptions,
                tlsHandshakeTimeout: tlsHandshakeTimeout));
        }

        /// <summary>Legacy TCP-with-TLS overload that binds to a parsed IP address.</summary>
        /// <remarks>Prefer the typed IPAddress TLS overload.</remarks>
        public SharpLinkServerBuilder UseTcp(
            int port,
            SslServerAuthenticationOptions tlsOptions,
            string ip,
            int backlog = 512,
            TimeSpan? tlsHandshakeTimeout = null)
        {
            ArgumentNullException.ThrowIfNull(tlsOptions);
            ArgumentException.ThrowIfNullOrWhiteSpace(ip);
            ValidateTcpPort(port);
            ValidateBacklog(backlog);

            var endPoint = new IPEndPoint(IPAddress.Parse(ip), port);
            return builder.UseTransport(new SocketServerTransportListener(
                endPoint,
                backlog,
                tlsOptions: tlsOptions,
                tlsHandshakeTimeout: tlsHandshakeTimeout));
        }

        /// <summary>Listens on a Unix-domain socket.</summary>
        public SharpLinkServerBuilder UseUds(string socketPath, int backlog = 512)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backlog);

            var endPoint = new UnixDomainSocketEndPoint(socketPath);
            return builder.UseTransport(new SocketServerTransportListener(endPoint, backlog));
        }

        /// <summary>Creates a one-client anonymous-pipe listener and handle offer.</summary>
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

        private static void ValidateTcpPort(int port)
        {
            if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
                throw new ArgumentOutOfRangeException(nameof(port));
        }

        private static void ValidateBacklog(int backlog)
            => ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backlog);
    }
}
