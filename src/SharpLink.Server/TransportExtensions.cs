
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

    }
}
