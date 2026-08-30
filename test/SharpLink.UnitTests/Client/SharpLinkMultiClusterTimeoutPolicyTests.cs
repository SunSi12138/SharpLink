using System.Reflection;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkMultiClusterTimeoutPolicyTests
{
    [Test]
    public async Task CoordinatorPolicySelectedAfterSlotShouldApplyAtBuild()
    {
        await using var client = CreateBuilder()
            .AddCluster(
                "dynamic",
                child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .UseRequestTimeout()
            .Build();

        var child = GetChildClient(client, "dynamic");
        Ensure(ReadHasRequestTimeout(child), "coordinator policy selected after AddCluster must reach the child");
        Ensure(ReadRequestTimeout(child) == TimeSpan.FromSeconds(30),
            "the inherited coordinator policy should use the recommended timeout");
    }

    [Test]
    public async Task LatestCoordinatorPolicyShouldApplyToPreviouslyAddedSlot()
    {
        await using var client = CreateBuilder()
            .UseRequestTimeout(TimeSpan.FromSeconds(5))
            .AddCluster(
                "dynamic",
                child => child.UseTransport(new TestClientTransportFactory()),
                slot => slot.AllowDynamicContracts = true)
            .UseRequestTimeout(TimeSpan.FromSeconds(9))
            .Build();

        Ensure(ReadRequestTimeout(GetChildClient(client, "dynamic")) == TimeSpan.FromSeconds(9),
            "a non-overridden child must resolve the coordinator's final policy at Build time");
    }

    [Test]
    public async Task SlotOverrideShouldWinWhenCoordinatorPolicyIsSelectedLater()
    {
        await using var client = CreateBuilder()
            .AddCluster(
                "dynamic",
                child => child
                    .UseTransport(new TestClientTransportFactory())
                    .UseRequestTimeout(TimeSpan.FromSeconds(7)),
                slot => slot.AllowDynamicContracts = true)
            .UseRequestTimeout()
            .Build();

        Ensure(ReadRequestTimeout(GetChildClient(client, "dynamic")) == TimeSpan.FromSeconds(7),
            "a slot's explicit timeout policy must override the coordinator policy regardless of configuration order");
    }

    [Test]
    public async Task BuildShouldRequireCoordinatorTimeoutPolicyEvenWhenSlotOverridesIt()
    {
        var builder = CreateBuilder()
            .AddCluster(
                "dynamic",
                child => child
                    .UseTransport(new TestClientTransportFactory())
                    .DisableRequestTimeout(),
                slot => slot.AllowDynamicContracts = true);

        try
        {
            _ = builder.Build();
            throw new Exception("expected explicit coordinator request-timeout policy failure");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("UseRequestTimeout()", StringComparison.Ordinal),
                "validation should name the recommended coordinator timeout API");
            Ensure(exception.Message.Contains("DisableRequestTimeout()", StringComparison.Ordinal),
                "validation should name the explicit coordinator disable API");
        }

        await Task.CompletedTask;
    }

    private static SharpLinkMultiClusterClientBuilder CreateBuilder()
        => SharpLinkMultiClusterClientBuilder.Create()
            .UseGeneratedDiscoverySources(
                new FixedGeneratedManifestSource([]),
                new FixedGeneratedClusterRouteSource([]));

    private static SharpLinkClient GetChildClient(
        ISharpLinkMultiClusterClient client,
        SharpLinkClusterKey cluster)
    {
        var coordinator = (SharpLinkMultiClusterClient)client;
        var snapshot = (MultiClusterSnapshot)typeof(SharpLinkMultiClusterClient)
            .GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(coordinator)!;
        return (SharpLinkClient)snapshot.Clusters[cluster].Client;
    }

    private static bool ReadHasRequestTimeout(SharpLinkClient client)
        => (bool)(typeof(SharpLinkClient)
            .GetField("_hasRequestTimeout", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client) ?? false);

    private static TimeSpan ReadRequestTimeout(SharpLinkClient client)
        => (TimeSpan)(typeof(SharpLinkClient)
            .GetField("_requestTimeoutValue", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client) ?? default(TimeSpan));

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
