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
    }
}
