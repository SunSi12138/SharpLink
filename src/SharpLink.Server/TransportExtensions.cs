

using System.IO;

namespace SharpLink.Server;

public static class TransportExtensions
{
    extension(SharpLinkServerBuilder builder)
    {
        public SharpLinkServerBuilder UseNamedPipe(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return builder.UseTransport(new NamedPipeTransport(
                name,
                isServer: true,
                maxServerInstances: NamedPipeServerStream.MaxAllowedServerInstances));
        }

        public SharpLinkServerBuilder UseTcp(int port, string ip = "0.0.0.0", int backlog = 512)
        {
            if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
                throw new ArgumentOutOfRangeException(nameof(port));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backlog);

            var endPoint = new IPEndPoint(IPAddress.Parse(ip), port);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(endPoint);
            socket.Listen(backlog);
            return builder.UseTransport(new SocketTransport(socket));
        }

        public SharpLinkServerBuilder UseUds(string socketPath, int backlog = 100)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backlog);

            if (File.Exists(socketPath))
                File.Delete(socketPath);

            var endPoint = new UnixDomainSocketEndPoint(socketPath);
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(endPoint);
            socket.Listen(backlog);
            return builder.UseTransport(new SocketTransport(socket));
        }

        public SharpLinkServerBuilder UseAnonymousPipe(PipeStream input, PipeStream output)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(output);
            return builder.UseTransport(new AnonymousPipeTransport(input, output));
        }
    }
}
