using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkMultiClusterStatusQueryTests : SharpLinkMultiClusterClientTestBase
{
    [Test]
    public async Task TryGetClusterStatusShouldReturnConfiguredSlotSnapshot()
    {
        await using var client = CreateStaticBuilder()
            .AddCluster("orders", child => child.UseTransport(new TestClientTransportFactory()))
            .Build();

        Ensure(client.TryGetClusterStatus("orders", out var status),
            "configured cluster status query should succeed");
        Ensure(status.Cluster == new SharpLinkClusterKey("orders"),
            "status snapshot should preserve the queried cluster key");
        Ensure(status.ConnectionState == SharpLinkConnectionState.Created,
            "new child should expose its legacy Created connection state");
        Ensure(status.RuntimeState == SharpLinkClusterState.Inactive,
            "Created should project to the inactive runtime state");
        Ensure(status.Readiness == SharpLinkReadinessState.NotReady,
            "Created should project to not-ready");
    }

    [Test]
    public async Task TryGetClusterStatusShouldReturnFalseForUnknownValidCluster()
    {
        await using var client = CreateStaticBuilder()
            .AddCluster("orders", child => child.UseTransport(new TestClientTransportFactory()))
            .Build();

        var found = client.TryGetClusterStatus("search", out var status);

        Ensure(!found, "a valid but absent cluster should be an expected query miss");
        Ensure(status == default, "an absent cluster should return the default status snapshot");
    }

    [Test]
    public async Task TryGetClusterStatusShouldRejectDefaultClusterKey()
    {
        await using var client = CreateStaticBuilder()
            .AddCluster("orders", child => child.UseTransport(new TestClientTransportFactory()))
            .Build();

        await EnsureThrows<ArgumentException>(() =>
        {
            _ = client.TryGetClusterStatus(default, out _);
            return Task.CompletedTask;
        });
    }
}
