namespace SharpLink.Client;

public static class TransportExtensions
{ 
    extension(SharpClientBuilder builder)
    {
        public SharpClientBuilder UseNamedPipe(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return builder.UseTransport(new NamedPipeTransport(name, isServer: false));
        }

        public SharpClientBuilder UseTcp(string ip, int port)
        {
            if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
                throw new ArgumentOutOfRangeException(nameof(port));

            var endPoint = new IPEndPoint(IPAddress.Parse(ip), port);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            return builder.UseTransport(new SocketTransport(socket, endPoint));
        }

        public SharpClientBuilder UseUds(string socketPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
            var endPoint = new UnixDomainSocketEndPoint(socketPath);
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            return builder.UseTransport(new SocketTransport(socket, endPoint));
        }

        public SharpClientBuilder UseAnonymousPipe(string inHandle, string outHandle)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(inHandle);
            ArgumentException.ThrowIfNullOrWhiteSpace(outHandle);
            var input = new AnonymousPipeClientStream(PipeDirection.In, inHandle);
            var output = new AnonymousPipeClientStream(PipeDirection.Out, outHandle);
            return builder.UseTransport(new AnonymousPipeTransport(input, output));
        }
    }
}
