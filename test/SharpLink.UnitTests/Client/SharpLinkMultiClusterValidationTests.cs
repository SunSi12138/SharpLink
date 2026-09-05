using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkMultiClusterValidationTests : SharpLinkMultiClusterClientTestBase
{
    [Test]
    public Task EmptySlotShouldRequireExplicitDynamicOptIn()
    {
        var builder = CreateDynamicBuilder()
            .AddCluster("dynamic", child => child.UseTransport(new TestClientTransportFactory()));

        return EnsureThrows<InvalidOperationException>(() =>
        {
            _ = builder.Build();
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task UnknownContractShouldFailWithoutSelectingAnotherCluster()
    {
        await using var client = CreateStaticBuilder()
            .AddCluster("orders", child => child.UseTransport(new TestClientTransportFactory()))
            .Build();

        await EnsureThrows<InvalidOperationException>(() =>
        {
            _ = client.Get<IUnroutedContract>();
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task BuildShouldRejectZeroClustersAndConnectionBudgetOverflow()
    {
        await EnsureThrows<InvalidOperationException>(() =>
        {
            _ = CreateDynamicBuilder().Build();
            return Task.CompletedTask;
        });

        await EnsureThrows<InvalidOperationException>(() =>
        {
            _ = CreateStaticBuilder()
                .Configure(options => options.MaxTotalConfiguredConnections = 1)
                .AddCluster("orders", child => child.UseTransport(new TestClientTransportFactory()))
                .AddCluster("plugins", child => child.UseTransport(new TestClientTransportFactory()),
                    slot => slot.AllowDynamicContracts = true)
                .Build();
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task SingleEndpointSlotsShouldUseTheirFixedConnectionBudget()
    {
        await using var client = CreateStaticBuilder()
            .Configure(options => options.MaxTotalConfiguredConnections = 2)
            .AddCluster("orders", child => child.UseEndpoint(
                Endpoint("orders", 5001),
                static _ => new TestClientTransportFactory()))
            .AddCluster("plugins", child => child.UseEndpoint(
                Endpoint("plugins", 5002),
                static _ => new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        Ensure(client.GetClusterState("orders") == SharpLinkConnectionState.Created,
            "single-endpoint slots fit their configured fixed-client budget");
    }

    [Test]
    public async Task SingleEndpointCollectionsShouldUseTheirFixedConnectionBudget()
    {
        await using var client = CreateStaticBuilder()
            .Configure(options => options.MaxTotalConfiguredConnections = 2)
            .AddCluster("orders", child => child.UseEndpoints(
                new OneShotEndpointEnumerable(Endpoint("orders", 5001)),
                static _ => new TestClientTransportFactory()))
            .AddCluster("plugins", child => child.UseEndpoints(
                new OneShotEndpointEnumerable(Endpoint("plugins", 5002)),
                static _ => new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .Build();

        Ensure(client.GetClusterState("orders") == SharpLinkConnectionState.Created,
            "one-endpoint collections must use their fixed-client budget without a second enumeration");
    }

    [Test]
    public async Task StaticEndpointClustersShouldUseTheirEffectiveConnectionBudget()
    {
        await using var client = CreateStaticBuilder()
            .Configure(options => options.MaxTotalConfiguredConnections = 2)
            .AddCluster("orders", child => child
                .UseEndpoints(
                    [Endpoint("orders-a", 5001), Endpoint("orders-b", 5002)],
                    static _ => new TestClientTransportFactory())
                .UseCluster(static options =>
                {
                    options.MaxConnections = 4;
                    options.MaxConnectionsPerEndpoint = 1;
                }))
            .Build();

        Ensure(client.GetClusterState("orders") == SharpLinkConnectionState.Created,
            "a static cluster must count its endpoint-capped connection capacity during coordinator preflight");
    }
}
