using System.Collections.Generic;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

/// <summary>
/// Test convenience around the production Client Builder. It intentionally has no direct Client
/// construction path: every returned runtime has completed Builder compile, materialization, and
/// ownership transfer exactly as production code does.
/// </summary>
internal static class ClientBuilderTestHelper
{
    internal static SharpLinkClient Build(
        IClientTransportFactory transport,
        Action<SharpClientBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var builder = CreateDefaultBuilder();
        builder.UseTransport(transport);
        configure?.Invoke(builder);
        return Materialize(builder);
    }

    internal static SharpLinkClient BuildEndpoint(
        SharpLinkEndpoint endpoint,
        IClientTransportFactory transport,
        Action<SharpClientBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(transport);
        var builder = CreateDefaultBuilder();
        builder.UseEndpoint(endpoint, _ => transport);
        configure?.Invoke(builder);
        return Materialize(builder);
    }

    internal static SharpLinkClient BuildStatic(
        IReadOnlyList<StaticEndpointConfiguration> configurations,
        Action<SharpClientBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(configurations);
        var endpoints = new SharpLinkEndpoint[configurations.Count];
        var transports = new Dictionary<string, Queue<IClientTransportFactory>>(
            configurations.Count,
            StringComparer.Ordinal);
        for (var index = 0; index < configurations.Count; index++)
        {
            var configuration = configurations[index] ?? throw new ArgumentException(
                "Static endpoint configurations cannot contain null.", nameof(configurations));
            endpoints[index] = configuration.Endpoint;
            if (!transports.TryGetValue(configuration.Endpoint.Id, out var endpointTransports))
            {
                endpointTransports = new Queue<IClientTransportFactory>();
                transports.Add(configuration.Endpoint.Id, endpointTransports);
            }
            endpointTransports.Enqueue(configuration.TransportFactory);
        }

        var builder = CreateDefaultBuilder();
        builder.UseEndpoints(
            endpoints,
            endpoint => transports.TryGetValue(endpoint.Id, out var endpointTransports) && endpointTransports.Count != 0
                ? endpointTransports.Dequeue()
                : throw new InvalidOperationException($"No test transport was configured for endpoint '{endpoint.Id}'."));
        configure?.Invoke(builder);
        return Materialize(builder);
    }

    internal static SharpLinkClient BuildDynamic(
        ISharpLinkEndpointResolver resolver,
        SharpLinkEndpointTransportFactory transportFactory,
        Action<SharpClientBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(transportFactory);
        var builder = CreateDefaultBuilder();
        builder.UseEndpointResolver(resolver, transportFactory);
        configure?.Invoke(builder);
        return Materialize(builder);
    }

    private static SharpClientBuilder CreateDefaultBuilder()
        // Historical direct constructors had no default request timeout. Individual migrated tests
        // opt in through UseRequestTimeout when that behavior is part of the scenario.
        => SharpClientBuilder.Create().DisableRequestTimeout();

    private static SharpLinkClient Materialize(SharpClientBuilder builder)
    {
        // This is the production multi-cluster child path as well: Compile freezes the explicit
        // empty manifest snapshot used by these isolated runtime tests, then Materialize transfers
        // ownership through the same transaction as normal Build.
        var plan = builder.CompileForMultiCluster([]);
        return builder.MaterializeCompiledPlan(plan) as SharpLinkClient ?? throw new InvalidOperationException(
            "The production Client Builder did not return its concrete runtime implementation.");
    }
}
