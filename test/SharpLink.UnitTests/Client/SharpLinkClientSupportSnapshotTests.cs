using System.Text.Json;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientSupportSnapshotTests
{
    [Test]
    public async Task DisconnectedSnapshotShouldRedactEndpointIdentityAndMetadata()
    {
        const string endpointSecret = "customer-prod-secret-endpoint";
        const string hostSecret = "private-db.internal.example";
        const string authoritySecret = "credential-user@private.example";
        const string metadataSecret = "api-key-super-secret-value";
        var endpoint = new SharpLinkEndpoint
        {
            Id = endpointSecret,
            Address = new SharpLinkTcpAddress(hostSecret, 7443),
            Authority = authoritySecret,
            Attributes = new Dictionary<string, string>
            {
                ["credential"] = metadataSecret
            }
        };
        await using var client = ClientBuilderTestHelper.BuildEndpoint(
            endpoint,
            new TestClientTransportFactory());

        var snapshot = client.GetDiagnosticSnapshot();
        Ensure(snapshot.SchemaVersion == SharpLinkClientSupportSnapshot.CurrentSchemaVersion,
            "schema version");
        Ensure(snapshot.Topology.Kind == SharpLinkSupportTopologyKind.Fixed, "fixed topology kind");
        Ensure(snapshot.Topology.TotalEndpoints == 1 && snapshot.Topology.CapturedEndpoints == 1,
            "fixed endpoint count");
        Ensure(snapshot.Topology.TotalConnections == 0, "disconnected connection count");
        Ensure(snapshot.Topology.Endpoints[0].SafeId == "endpoint-0001", "safe endpoint ordinal");
        Ensure(snapshot.Topology.Endpoints[0].Transport == SharpLinkSupportTransportKind.Tcp,
            "transport category");
        Ensure(snapshot.Topology.Endpoints[0].AuthorityConfigured, "authority presence only");

        var json = client.ExportDiagnosticSnapshotJson();
        Ensure(!json.Contains(endpointSecret, StringComparison.Ordinal), "endpoint id redacted");
        Ensure(!json.Contains(hostSecret, StringComparison.Ordinal), "host redacted");
        Ensure(!json.Contains(authoritySecret, StringComparison.Ordinal), "authority redacted");
        Ensure(!json.Contains(metadataSecret, StringComparison.Ordinal), "metadata redacted");
        Ensure(json.Contains("endpoint-0001", StringComparison.Ordinal), "safe endpoint id exported");
    }

    [Test]
    public async Task StartedOfflineSnapshotShouldSeparateLifecycleReadinessAndConnectivity()
    {
        const string secret = "support-snapshot-review-secret";
        await using var client = ClientBuilderTestHelper.Build(
            new FailingTransportFactory(new InvalidOperationException(secret)));

        await client.StartAsync();
        for (var attempt = 0; attempt < 100 && client.State != SharpLinkConnectionState.Reconnecting; attempt++)
            await Task.Delay(10);

        var snapshot = client.GetDiagnosticSnapshot();
        Ensure(snapshot.SchemaVersion == SharpLinkClientSupportSnapshot.CurrentSchemaVersion,
            "lifecycle snapshot schema version");
        Ensure(snapshot.SchemaVersion == 1, "the first published support snapshot uses schema v1");
        Ensure(snapshot.LifecycleState == SharpLinkClientLifecycleState.Running,
            "runtime remains running while remote is unavailable");
        Ensure(snapshot.ReadinessState == SharpLinkReadinessState.NotReady,
            "remote unavailability is reported independently as not ready");
        Ensure(snapshot.ClusterState == client.ClusterState,
            "cluster state uses the independent connectivity domain");
        Ensure(snapshot.ConnectionState == client.State,
            "legacy connection state remains available independently");
        Ensure(snapshot.ConnectionState == SharpLinkConnectionState.Reconnecting,
            "failed initial connectivity is reported as reconnecting");
        Ensure(snapshot.ClusterState == SharpLinkClusterState.Reconnecting,
            "cluster connectivity is reported as reconnecting");

        var json = client.ExportDiagnosticSnapshotJson();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Ensure(root.GetProperty("schemaVersion").GetInt32() == 1,
            "JSON exports the first published support schema v1");
        Ensure(Enum.Parse<SharpLinkClientLifecycleState>(
                root.GetProperty("lifecycleState").GetString()!, ignoreCase: true) == snapshot.LifecycleState,
            "JSON exports lifecycle state value");
        Ensure(Enum.Parse<SharpLinkReadinessState>(
                root.GetProperty("readinessState").GetString()!, ignoreCase: true) == snapshot.ReadinessState,
            "JSON exports readiness state value");
        Ensure(Enum.Parse<SharpLinkClusterState>(
                root.GetProperty("clusterState").GetString()!, ignoreCase: true) == snapshot.ClusterState,
            "JSON exports cluster state value");
        Ensure(Enum.Parse<SharpLinkConnectionState>(
                root.GetProperty("connectionState").GetString()!, ignoreCase: true) == snapshot.ConnectionState,
            "JSON exports legacy connection state value");
        Ensure(!json.Contains(secret, StringComparison.Ordinal),
            "new diagnostic state fields do not weaken failure redaction");
    }

    [Test]
    public async Task ReadySnapshotShouldReuseNegotiatedAndResourceOwners()
    {
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.CancellationReason);
        await using var client = ClientBuilderTestHelper.Build(transport);
        await client.ConnectAsync();

        var snapshot = client.GetDiagnosticSnapshot();
        Ensure(snapshot.Readiness.ReadyConnections == 1, "readiness connection count");
        Ensure(snapshot.Topology.TotalConnections == 1 && snapshot.Topology.CapturedConnections == 1,
            "ready connection inventory");
        var connection = snapshot.Topology.Connections[0];
        Ensure(connection.State == SharpLinkSupportConnectionState.Ready, "ready connection state");
        Ensure(connection.CanAcceptCalls, "ready connection acceptance");
        Ensure(connection.Resources.PendingRequestCapacity > 0, "pending request capacity");
        Ensure(connection.Resources.SendQueueLimitBytes > 0, "send queue limit");
        Ensure(connection.Resources.StreamLimit > 0, "stream limit");
        Ensure(connection.Negotiation.ProtocolMajor == 2, "protocol major");
        Ensure(connection.Negotiation.ProtocolMinor is not null, "protocol minor negotiated");
        Ensure(snapshot.Resources.ReadyConnections == 1, "aggregate ready connection count");
        Ensure(snapshot.Resources.PendingRequests == 0, "aggregate pending requests");
        Ensure(snapshot.Resources.ActiveStreams == 0, "aggregate active streams");
    }

    [Test]
    public async Task StaticSnapshotShouldBoundEndpointDetailsAndExposeTotals()
    {
        var configurations = new List<StaticEndpointConfiguration>();
        for (var index = 0; index < 6; index++)
        {
            configurations.Add(new StaticEndpointConfiguration(
                new SharpLinkEndpoint
                {
                    Id = $"secret-endpoint-{index}",
                    Address = new SharpLinkTcpAddress($"secret-{index}.internal", 5001 + index),
                    Attributes = new Dictionary<string, string>
                    {
                        ["tenant"] = $"secret-tenant-{index}"
                    }
                },
                new TestClientTransportFactory()));
        }
        await using var client = ClientBuilderTestHelper.BuildStatic(configurations);

        var snapshot = client.GetDiagnosticSnapshot(new SharpLinkClientSupportSnapshotOptions
        {
            MaxEndpoints = 2,
            MaxConnections = 1
        });
        Ensure(snapshot.Topology.Kind == SharpLinkSupportTopologyKind.Static, "static topology kind");
        Ensure(snapshot.Topology.TotalEndpoints == 6, "total endpoints retained as scalar");
        Ensure(snapshot.Topology.CapturedEndpoints == 2 && snapshot.Topology.EndpointsTruncated,
            "endpoint details bounded");
        Ensure(snapshot.Topology.TotalConnections == 0 && !snapshot.Topology.ConnectionsTruncated,
            "disconnected connection totals");

        var json = client.ExportDiagnosticSnapshotJson(new SharpLinkClientSupportSnapshotOptions
        {
            MaxEndpoints = 2,
            MaxConnections = 1
        });
        for (var index = 0; index < 6; index++)
        {
            Ensure(!json.Contains($"secret-endpoint-{index}", StringComparison.Ordinal), "endpoint id redacted");
            Ensure(!json.Contains($"secret-{index}.internal", StringComparison.Ordinal), "endpoint host redacted");
            Ensure(!json.Contains($"secret-tenant-{index}", StringComparison.Ordinal), "endpoint metadata redacted");
        }
    }

    [Test]
    public async Task JsonExportShouldEnforceHardUtf8SizeLimit()
    {
        var configurations = new List<StaticEndpointConfiguration>();
        for (var index = 0; index < 32; index++)
        {
            configurations.Add(new StaticEndpointConfiguration(
                new SharpLinkEndpoint
                {
                    Id = $"endpoint-{index}",
                    Address = new SharpLinkTcpAddress("127.0.0.1", 5100 + index)
                },
                new TestClientTransportFactory()));
        }
        await using var client = ClientBuilderTestHelper.BuildStatic(configurations);

        var threw = false;
        try
        {
            _ = client.ExportDiagnosticSnapshotJson(new SharpLinkClientSupportSnapshotOptions
            {
                MaxEndpoints = 32,
                MaxConnections = 1,
                MaxJsonBytes = 4096,
                WriteIndented = true
            });
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        Ensure(threw, "oversized JSON export must fail closed");
    }

    [Test]
    public async Task LastConnectionFailureShouldExportOnlyCoarseSafeFields()
    {
        const string secretMessage = "Bearer top-secret-token tenant=customer-a";
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        client.RecordConnectionFailure(
            SharpLinkConnectionFailureStage.Handshake,
            new InvalidOperationException(secretMessage),
            "endpoint-0001");

        var snapshot = client.GetDiagnosticSnapshot();
        var failure = snapshot.LastConnectionFailure;
        Ensure(failure is not null, "last failure present");
        Ensure(failure!.Stage == SharpLinkConnectionFailureStage.Handshake, "failure stage");
        Ensure(failure.Classification == SharpLinkConnectionFailureClass.Internal, "coarse classification");
        Ensure(failure.ExceptionType == nameof(Exception), "unknown exception type is normalized");
        Ensure(failure.EndpointSafeId == "endpoint-0001", "safe endpoint reference");

        var json = client.ExportDiagnosticSnapshotJson();
        Ensure(!json.Contains(secretMessage, StringComparison.Ordinal), "exception message redacted");
        Ensure(!json.Contains("top-secret-token", StringComparison.Ordinal), "token redacted");
    }

    [Test]
    public async Task SnapshotShouldRemainAvailableAfterStopAndRepeatedCapture()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);
        await client.ConnectAsync();
        await client.StopAsync();

        for (var index = 0; index < 16; index++)
        {
            var snapshot = client.GetDiagnosticSnapshot();
            Ensure(snapshot.SchemaVersion == SharpLinkClientSupportSnapshot.CurrentSchemaVersion,
                "post-stop schema version");
            Ensure(snapshot.Topology.CapturedEndpoints <= SharpLinkClientSupportSnapshotOptions.DefaultMaxEndpoints,
                "post-stop endpoint bound");
            Ensure(snapshot.Topology.CapturedConnections <= SharpLinkClientSupportSnapshotOptions.DefaultMaxConnections,
                "post-stop connection bound");
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class FailingTransportFactory(Exception failure) : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(failure);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
