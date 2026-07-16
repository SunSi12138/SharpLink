using SharpLink.Runtime;
using SharpLink.Abstractions;
using System;
using System.Threading.Tasks;
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
            .UseSerializer(MemoryPackCodec.Resolver)
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
            .UseSerializer(MemoryPackCodec.Resolver)
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

    public static async Task<LocalHarness> CreateLocalHarness(
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

        var serverBuilder = configure(SharpLinkServerBuilder.Create())
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseAnonymousPipe()
            .UseHeartbeat(TimeSpan.FromSeconds(heartbeatCheckIntervalSeconds), TimeSpan.FromSeconds(heartbeatTimeoutSeconds));
        var anonymousPipeAllocator = (IAnonymousPipeAllocator)serverBuilder.Transport!;
        var serverAnonymous = serverBuilder.Build();
        
        var (inHandler, outHandler) = await anonymousPipeAllocator.AllocateAsync();
        var clientAnonymous = SharpClientBuilder.Create()
            .UseTransport(new AnonymousPipeClientTransportFactory(inHandler, outHandler))
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseHeartbeat(TimeSpan.FromSeconds(heartbeatIntervalSeconds), TimeSpan.FromSeconds(heartbeatTimeoutSeconds))
            .Build();
        
        return new LocalHarness(serverAnonymous, clientAnonymous, static () => { });
    }
}
