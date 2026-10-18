using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpLink.Client;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Builder;

public sealed class Issue805HostLoggerBoundaryTests
{
    [Test]
    public void HostFallbackMustNotBePartOfThePublicBuilderApi()
    {
        foreach (var builder in new[]
        {
            typeof(SharpClientBuilder),
            typeof(SharpLinkServerBuilder),
            typeof(SharpLinkMultiClusterClientBuilder)
        })
        {
            if (builder.GetMethod("UseLoggerFactoryIfUnset", BindingFlags.Instance | BindingFlags.Public) is not null ||
                builder.GetMethod("UseLoggerFactoryIfUnset", BindingFlags.Instance | BindingFlags.NonPublic) is null)
                throw new Exception($"Hosting-only fallback must be internal: {builder.FullName}.");
            if (builder.GetMethod("UseLoggerFactory", BindingFlags.Instance | BindingFlags.Public) is null)
                throw new Exception($"Explicit logger configuration must remain public: {builder.FullName}.");
        }
    }

    [Test]
    public void SingleClientAndServerPreserveExplicitLoggerInBothConfigurationOrders()
    {
        using var host = new MarkerLoggerFactory();
        using var user = new MarkerLoggerFactory();

        var clientFirst = SharpClientBuilder.Create().UseLoggerFactory(user);
        clientFirst.UseLoggerFactoryIfUnset(host);
        EnsureLogger(clientFirst, user);

        var clientAfter = SharpClientBuilder.Create();
        clientAfter.UseLoggerFactoryIfUnset(host);
        clientAfter.UseLoggerFactory(user);
        EnsureLogger(clientAfter, user);

        var serverFirst = SharpLinkServerBuilder.Create().UseLoggerFactory(user);
        serverFirst.UseLoggerFactoryIfUnset(host);
        EnsureLogger(serverFirst, user);

        var serverAfter = SharpLinkServerBuilder.Create();
        serverAfter.UseLoggerFactoryIfUnset(host);
        serverAfter.UseLoggerFactory(user);
        EnsureLogger(serverAfter, user);
    }

    [Test]
    public async Task MultiClusterBuildShouldHonorChildCoordinatorHostPrecedence()
    {
        using var host = new MarkerLoggerFactory();
        using var coordinator = new MarkerLoggerFactory();
        using var childLogger = new MarkerLoggerFactory();

        foreach (var coordinatorFirst in new[] { true, false })
        {
            SharpClientBuilder first = null!;
            SharpClientBuilder second = null!;
            var multi = SharpLinkMultiClusterClientBuilder.Create()
                .UseGeneratedDiscoverySources(
                    new FixedGeneratedManifestSource([]),
                    new FixedGeneratedClusterRouteSource([]))
                .DisableRequestTimeout()
                .AddCluster("first",
                    child =>
                    {
                        first = child;
                        child.UseTransport(new TestClientTransportFactory());
                    }, slot => slot.AllowDynamicContracts = true)
                .AddCluster("second",
                    child =>
                    {
                        second = child;
                        child.UseTransport(new TestClientTransportFactory())
                            .UseLoggerFactory(childLogger);
                    }, slot => slot.AllowDynamicContracts = true);

            if (coordinatorFirst)
            {
                multi.UseLoggerFactory(coordinator);
                multi.UseLoggerFactoryIfUnset(host);
            }
            else
            {
                multi.UseLoggerFactoryIfUnset(host);
                multi.UseLoggerFactory(coordinator);
            }

            await using var client = multi.Build();
            EnsureLogger(multi, coordinator);
            EnsureLogger(first, coordinator);
            EnsureLogger(second, childLogger);
        }

        SharpClientBuilder fallbackChild = null!;
        var fallback = SharpLinkMultiClusterClientBuilder.Create()
            .UseGeneratedDiscoverySources(
                new FixedGeneratedManifestSource([]),
                new FixedGeneratedClusterRouteSource([]))
            .DisableRequestTimeout()
            .AddCluster("host-child",
                child =>
                {
                    fallbackChild = child;
                    child.UseTransport(new TestClientTransportFactory());
                }, slot => slot.AllowDynamicContracts = true);
        fallback.UseLoggerFactoryIfUnset(host);
        await using var hostedClient = fallback.Build();
        EnsureLogger(fallback, host);
        EnsureLogger(fallbackChild, host);
    }

    private static void EnsureLogger(object builder, ILoggerFactory expected)
    {
        var field = builder.GetType().GetField(
            "_loggerFactory", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception($"Logger state missing: {builder.GetType().Name}");
        if (!ReferenceEquals(field.GetValue(builder), expected))
            throw new Exception($"Wrong logger precedence on {builder.GetType().Name}.");
    }

    private sealed class MarkerLoggerFactory : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => NullLogger.Instance;
        public void Dispose() { }
    }
}
