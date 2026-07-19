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
        Func<SharpLinkServerBuilder, SharpLinkServerBuilder> configure,
        SharpLinkPerformanceProfile performanceProfile = SharpLinkPerformanceProfile.Balanced,
        string? sharedMemoryName = null,
        int? sharedMemoryCapacity = null,
        int? sharedMemorySpinCount = null)
    {
        var builder = configure(SharpLinkServerBuilder.Create())
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseRuntime(options => options.PerformanceProfile = performanceProfile)
            .UseHeartbeat(TimeSpan.FromSeconds(heartbeatCheckIntervalSeconds), TimeSpan.FromSeconds(heartbeatTimeoutSeconds));

        return transport switch
        {
            TransportMode.Tcp => builder.UseTcp(port, bindIp).Build(),
            TransportMode.Uds => builder.UseUds(udsPath).Build(),
            TransportMode.NamedPipe => builder.UseNamedPipe(pipeName).Build(),
            TransportMode.SharedMemory => builder.UseSharedMemory(
                RequireSharedMemoryName(sharedMemoryName),
                options => ConfigureSharedMemory(options, sharedMemoryCapacity, sharedMemorySpinCount)).Build(),
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
        int heartbeatTimeoutSeconds,
        int minConnections,
        int maxConnections,
        SharpLinkPerformanceProfile performanceProfile = SharpLinkPerformanceProfile.Balanced,
        bool disableRequestTimeout = false,
        TimeSpan? requestTimeout = null,
        string? sharedMemoryName = null,
        int? sharedMemoryCapacity = null,
        int? sharedMemorySpinCount = null)
    {
        var builder = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseRuntime(options => options.PerformanceProfile = performanceProfile)
            .UseConnectionPool(options =>
            {
                options.MinConnections = minConnections;
                options.MaxConnections = maxConnections;
            })
            .UseHeartbeat(TimeSpan.FromSeconds(heartbeatIntervalSeconds), TimeSpan.FromSeconds(heartbeatTimeoutSeconds));

        if (disableRequestTimeout)
            builder.DisableRequestTimeout();
        else if (requestTimeout is { } timeout)
            builder.UseRequestTimeout(timeout);

        return transport switch
        {
            TransportMode.Tcp => builder.UseTcp(host, port).Build(),
            TransportMode.Uds => builder.UseUds(udsPath).Build(),
            TransportMode.NamedPipe => builder.UseNamedPipe(pipeName).Build(),
            TransportMode.SharedMemory => builder.UseSharedMemory(
                RequireSharedMemoryName(sharedMemoryName),
                options => ConfigureSharedMemory(options, sharedMemoryCapacity, sharedMemorySpinCount)).Build(),
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
        int minConnections,
        int maxConnections,
        Func<SharpLinkServerBuilder, SharpLinkServerBuilder> configure,
        SharpLinkPerformanceProfile performanceProfile = SharpLinkPerformanceProfile.Balanced,
        bool disableRequestTimeout = false,
        TimeSpan? requestTimeout = null,
        string? sharedMemoryName = null,
        int? sharedMemoryCapacity = null,
        int? sharedMemorySpinCount = null)
    {
        if (transport != TransportMode.AnonymousPipe)
        {
            var server = CreateServer(
                transport, bindIp, port, udsPath, pipeName,
                heartbeatCheckIntervalSeconds, heartbeatTimeoutSeconds, configure, performanceProfile,
                sharedMemoryName, sharedMemoryCapacity, sharedMemorySpinCount);
            var client = CreateClient(
                transport,
                host,
                port,
                udsPath,
                pipeName,
                heartbeatIntervalSeconds,
                heartbeatTimeoutSeconds,
                minConnections,
                maxConnections,
                performanceProfile,
                disableRequestTimeout,
                requestTimeout,
                sharedMemoryName,
                sharedMemoryCapacity,
                sharedMemorySpinCount);
            return new LocalHarness(server, client, static () => { });
        }

        var serverBuilder = configure(SharpLinkServerBuilder.Create())
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseRuntime(options => options.PerformanceProfile = performanceProfile)
            .UseAnonymousPipe()
            .UseHeartbeat(TimeSpan.FromSeconds(heartbeatCheckIntervalSeconds), TimeSpan.FromSeconds(heartbeatTimeoutSeconds));
        var anonymousPipeAllocator = (IAnonymousPipeAllocator)serverBuilder.Transport!;
        var serverAnonymous = serverBuilder.Build();
        
        var (inHandler, outHandler) = await anonymousPipeAllocator.AllocateAsync();
        var clientAnonymous = SharpClientBuilder.Create()
            .UseTransport(new AnonymousPipeClientTransportFactory(inHandler, outHandler))
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseRuntime(options => options.PerformanceProfile = performanceProfile)
            .UseConnectionPool(options =>
            {
                options.MinConnections = minConnections;
                options.MaxConnections = maxConnections;
            })
            .UseHeartbeat(TimeSpan.FromSeconds(heartbeatIntervalSeconds), TimeSpan.FromSeconds(heartbeatTimeoutSeconds));

        if (disableRequestTimeout)
            clientAnonymous.DisableRequestTimeout();
        else if (requestTimeout is { } timeout)
            clientAnonymous.UseRequestTimeout(timeout);
        
        return new LocalHarness(serverAnonymous, clientAnonymous.Build(), static () => { });
    }

    private static string RequireSharedMemoryName(string? name)
        => !string.IsNullOrWhiteSpace(name)
            ? name
            : throw new ArgumentException("Shared-memory transport requires a logical endpoint name.");

    private static void ConfigureSharedMemory(
        SharedMemoryTransportOptions options,
        int? capacity,
        int? spinCount)
    {
        options.CapacityPerDirectionBytes = capacity;
        options.SpinCount = spinCount;
    }
}
