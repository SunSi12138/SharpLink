using System.Reflection;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Sdk;

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
            "new child should expose its canonical inactive runtime state");
        Ensure(status.Readiness == SharpLinkReadinessState.NotReady,
            "new child should expose its canonical not-ready state");
    }

    [Test]
    public async Task LegacyConnectStatusShouldPreserveCanonicalChildReadiness()
    {
        await using var client = CreateStaticBuilder()
            .AddCluster("orders", child => child.UseTransport(new TestClientTransportFactory()))
            .Build();

        await client.ConnectAsync();

        Ensure(client.GetClusterState("orders") == SharpLinkConnectionState.Ready,
            "legacy ConnectAsync should be able to publish the child connection state as Ready");
        Ensure(client.GetClusterReadiness("orders") == SharpLinkReadinessState.NotReady,
            "legacy ConnectAsync must not implicitly start the child lifecycle or publish canonical readiness");
        Ensure(client.TryGetClusterStatus("orders", out var status),
            "configured cluster status query should succeed after legacy ConnectAsync");
        Ensure(status.ConnectionState == client.GetClusterState("orders"),
            "status should preserve the independent legacy connection-state domain");
        Ensure(status.RuntimeState == client.GetClusterRuntimeState("orders"),
            "status should preserve the canonical cluster runtime-state domain");
        Ensure(status.Readiness == client.GetClusterReadiness("orders"),
            "status readiness must match the canonical child readiness getter rather than legacy connection state");
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

    [Test]
    public async Task LegacyCustomStatusFallbackShouldNotReclassifyArgumentExceptionAsMissing()
    {
        var legacy = new ArgumentRejectingLegacyMultiClusterClient();
        await using ISharpLinkMultiClusterClient client = legacy;

        await EnsureThrows<NotSupportedException>(() =>
        {
            _ = client.TryGetClusterStatus("orders", out _);
            return Task.CompletedTask;
        });

        Ensure(legacy.GetClusterStateCalls == 0,
            "the default status-query implementation must not probe legacy getters and guess exception meaning");
    }

    private sealed class ArgumentRejectingLegacyMultiClusterClient : ISharpLinkMultiClusterClient
    {
        internal int GetClusterStateCalls { get; private set; }

        public SharpLinkMultiClusterState State => SharpLinkMultiClusterState.Created;

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public TContract Get<TContract>() where TContract : IService
            => throw new NotSupportedException();

        public TContract GetWithMetadata<TContract>(SharpLinkMetadata metadata) where TContract : IService
            => throw new NotSupportedException();

        public SharpLinkConnectionState GetClusterState(SharpLinkClusterKey cluster)
        {
            GetClusterStateCalls++;
            throw new ArgumentException("Implementation-specific cluster precondition failed.", nameof(cluster));
        }

        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
            SharpLinkClusterKey cluster,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public SharpLinkAssemblyRegistrationResult RegisterAssembly(
            SharpLinkClusterKey cluster,
            Assembly assembly)
            => throw new NotSupportedException();

        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            SharpLinkClusterKey cluster,
            Assembly assembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
            SharpLinkClusterKey cluster,
            Assembly oldAssembly,
            Assembly newAssembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
