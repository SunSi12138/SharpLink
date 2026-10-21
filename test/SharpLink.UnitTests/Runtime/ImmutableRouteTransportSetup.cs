using System.Net;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.UnitTests.Runtime;

public static partial class ImmutableRouteTransportSupport
{
    internal static async Task<ServerFixture> StartServerAsync(Plugin plugin, bool admission = false)
    {
        var listener = new SocketServerTransportListener(new IPEndPoint(IPAddress.Loopback, 0));
        var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(listener).Build();
        try
        {
            server.EnableImmutableRoutesForTesting = true;
            var registered = server.RegisterAssembly(plugin.Assembly);
            Ensure(registered.Succeeded, $"register dynamic fixture: {registered.Error}");
            if (admission)
            {
                server.EnableAdmissionControl(options =>
                {
                    options.Global.UseConcurrency(1);
                    options.MaxQueuedCalls = 4;
                    options.MaxQueuedBytes = 4096;
                    options.MaxQueueDelay = Timeout;
                });
            }
            await server.StartAsync().AsTask().WaitAsync(Timeout);
            return new ServerFixture(server, (IPEndPoint)listener.LocalEndPoint!);
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    internal static SharpLinkClient CreateClient(IPEndPoint endpoint, IdentityObserver? observer = null)
    {
        var builder = SharpClientBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseEndpoint(new SharpLinkEndpoint
            {
                Id = "immutable-route-fixture",
                Address = new SharpLinkTcpAddress(endpoint.Address.ToString(), endpoint.Port)
            }, _ => new SocketClientTransportFactory(endpoint))
            .UseRequestTimeout(Timeout);
        if (observer is not null)
            builder.AddInterceptor(observer).UseEndpointAdmission(observer);
        var client = (SharpLinkClient)builder.Build();
        client.EnableImmutableRoutesForTesting = true;
        return client;
    }

    internal static async Task<RpcMethodDescriptor> WaitForNewRouteAsync(
        SharpLinkClient client, RpcMethodDescriptor old)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (true)
        {
            var prepared = client.PrepareMethodForTesting(Unary, AssemblyHash);
            if (prepared.WireRouteId != old.WireRouteId)
                return prepared;
            await Task.Delay(1, timeout.Token);
        }
    }

}
