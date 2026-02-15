using SharpLink.Runtime;
using SharpLink.Abstractions;
using System;
using System.IO;
using System.IO.Pipes;
using SharpLink.Client;
using SharpLink.Server;

namespace SharpLink.LoadTestBase;

public static class LoadTestTransportFactory
{
    public static ISharpLinkServer CreateServer(
        TransportMode transport,
        string bindIp,
        int port,
        string udsPath,
        string pipeName,
        int heartbeatCheckIntervalSeconds,
        int heartbeatTimeoutSeconds,
        Func<SharpLinkServerBuilder, SharpLinkServerBuilder> configure)
    {
        var builder = configure(SharpLinkServerBuilder.Create())
            .UseSerializer(new MemoryPackSerializerAdaptor())
            .UseHeartbeat(TimeSpan.FromSeconds(heartbeatCheckIntervalSeconds), TimeSpan.FromSeconds(heartbeatTimeoutSeconds));

        return transport switch
        {
            TransportMode.Tcp => builder.UseTcp(port, bindIp).Build(),
            TransportMode.Uds => builder.UseUds(udsPath).Build(),
            TransportMode.NamedPipe => builder.UseNamedPipe(pipeName).Build(),
            TransportMode.AnonymousPipe => throw new InvalidOperationException("Anonymous pipe transport only supports --mode local."),
            _ => throw new ArgumentOutOfRangeException(nameof(transport))
        };
    }

    public static ISharpLinkClient CreateClient(
        TransportMode transport,
        string host,
        int port,
        string udsPath,
        string pipeName,
        int heartbeatIntervalSeconds,
        int heartbeatTimeoutSeconds)
    {
        var builder = SharpClientBuilder.Create()
            .UseSerializer(new MemoryPackSerializerAdaptor())
            .UseHeartbeat(TimeSpan.FromSeconds(heartbeatIntervalSeconds), TimeSpan.FromSeconds(heartbeatTimeoutSeconds));

        return transport switch
        {
            TransportMode.Tcp => builder.UseTcp(host, port).Build(),
            TransportMode.Uds => builder.UseUds(udsPath).Build(),
            TransportMode.NamedPipe => builder.UseNamedPipe(pipeName).Build(),
            TransportMode.AnonymousPipe => throw new InvalidOperationException("Anonymous pipe transport only supports --mode local."),
            _ => throw new ArgumentOutOfRangeException(nameof(transport))
        };
    }

    public static LocalHarness CreateLocalHarness(
        TransportMode transport,
        string host,
        string bindIp,
        int port,
        string udsPath,
        string pipeName,
        int heartbeatIntervalSeconds,
        int heartbeatCheckIntervalSeconds,
        int heartbeatTimeoutSeconds,
        Func<SharpLinkServerBuilder, SharpLinkServerBuilder> configure)
    {
        if (transport != TransportMode.AnonymousPipe)
        {
            var server = CreateServer(transport, bindIp, port, udsPath, pipeName, heartbeatCheckIntervalSeconds, heartbeatTimeoutSeconds, configure);
            var client = CreateClient(transport, host, port, udsPath, pipeName, heartbeatIntervalSeconds, heartbeatTimeoutSeconds);
            return new LocalHarness(server, client, static () => { });
        }

        var serverInput = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var serverOutput = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var clientInHandle = serverOutput.GetClientHandleAsString();
        var clientOutHandle = serverInput.GetClientHandleAsString();

        var serverBuilder = configure(SharpLinkServerBuilder.Create())
            .UseSerializer(new MemoryPackSerializerAdaptor())
            .UseHeartbeat(TimeSpan.FromSeconds(heartbeatCheckIntervalSeconds), TimeSpan.FromSeconds(heartbeatTimeoutSeconds));
        var serverAnonymous = serverBuilder.UseAnonymousPipe(serverInput, serverOutput).Build();

        var clientAnonymous = SharpClientBuilder.Create()
            .UseAnonymousPipe(clientInHandle, clientOutHandle)
            .UseSerializer(new MemoryPackSerializerAdaptor())
            .UseHeartbeat(TimeSpan.FromSeconds(heartbeatIntervalSeconds), TimeSpan.FromSeconds(heartbeatTimeoutSeconds))
            .Build();

        return new LocalHarness(serverAnonymous, clientAnonymous, static () => { });
    }
}



