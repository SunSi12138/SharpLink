using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Text;
using System.Threading.Channels;
using SharpLink.Client;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Client;

[NotInParallel]
public sealed class SharpLinkClientSupportSnapshotAcceptanceTests
{
    [Test]
    public async Task FixedDialFailureShouldPublishSafeLastFailure()
    {
        const string secret = "dial-secret-customer-path";
        await using var client = ClientBuilderTestHelper.Build(
            new FailingTransportFactory(new IOException(secret)));

        await CaptureConnectFailureAsync(client);
        var snapshot = client.GetDiagnosticSnapshot();
        var failure = snapshot.LastConnectionFailure;
        Ensure(failure is not null, "dial failure snapshot");
        Ensure(failure!.Stage == SharpLinkConnectionFailureStage.Dial, "dial failure stage");
        Ensure(failure.Classification == SharpLinkConnectionFailureClass.Transport, "dial failure classification");
        Ensure(failure.ExceptionType == nameof(IOException), "dial failure type");
        Ensure(failure.EndpointSafeId == "endpoint-0001", "dial endpoint safe id");
        Ensure(!client.ExportDiagnosticSnapshotJson().Contains(secret, StringComparison.Ordinal),
            "dial exception message redacted");
    }

    [Test]
    public async Task FixedTlsFailureShouldPublishSafeLastFailure()
    {
        const string secret = "tls-certificate-subject-secret";
        await using var client = ClientBuilderTestHelper.Build(
            new FailingTransportFactory(new AuthenticationException(secret)));

        await CaptureConnectFailureAsync(client);
        var failure = client.GetDiagnosticSnapshot().LastConnectionFailure;
        Ensure(failure is not null, "TLS failure snapshot");
        Ensure(failure!.Stage == SharpLinkConnectionFailureStage.Tls, "TLS failure stage");
        Ensure(failure.Classification == SharpLinkConnectionFailureClass.Authentication,
            "TLS failure classification");
        Ensure(failure.ExceptionType == nameof(AuthenticationException), "TLS failure type");
        Ensure(!client.ExportDiagnosticSnapshotJson().Contains(secret, StringComparison.Ordinal),
            "TLS exception message redacted");
    }

    [Test]
    public async Task FixedHandshakeTimeoutShouldPublishHandshakeStageWithoutRawFailureText()
    {
        var transport = new SilentHandshakeTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport, builder =>
            builder.UseProtocol(options => options.HandshakeTimeout = TimeSpan.FromMilliseconds(20)));

        await CaptureConnectFailureAsync(client);
        var failure = client.GetDiagnosticSnapshot().LastConnectionFailure;
        Ensure(failure is not null, "handshake timeout snapshot");
        Ensure(failure!.Stage == SharpLinkConnectionFailureStage.Handshake, "handshake timeout stage");
        Ensure(failure.ErrorCode is not null, "handshake timeout safe error code");
        var json = client.ExportDiagnosticSnapshotJson();
        Ensure(!json.Contains("Handshake timed out", StringComparison.OrdinalIgnoreCase),
            "raw handshake exception message omitted");
    }

    [Test]
    public async Task StructuredAuthAndProtocolFailuresShouldUseSafeClassifications()
    {
        const string authSecret = "Authorization: Bearer auth-secret-value";
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());

        client.RecordConnectionFailure(
            SharpLinkConnectionFailureStage.Unknown,
            new SharpLinkException(SharpLinkErrorCode.AuthenticationRejected, authSecret),
            "endpoint-0001");
        var authentication = client.GetDiagnosticSnapshot().LastConnectionFailure;
        Ensure(authentication is not null, "authentication failure snapshot");
        Ensure(authentication!.Stage == SharpLinkConnectionFailureStage.Authentication,
            "authentication stage");
        Ensure(authentication.Classification == SharpLinkConnectionFailureClass.Authentication,
            "authentication classification");
        Ensure(authentication.ErrorCode == nameof(SharpLinkErrorCode.AuthenticationRejected),
            "authentication safe code");
        Ensure(!client.ExportDiagnosticSnapshotJson().Contains(authSecret, StringComparison.Ordinal),
            "authentication message redacted");

        const string protocolSecret = "protocol-secret-peer-metadata";
        client.RecordConnectionFailure(
            SharpLinkConnectionFailureStage.Unknown,
            new SharpLinkException(SharpLinkErrorCode.ProtocolViolation, protocolSecret),
            "endpoint-0001");
        var protocol = client.GetDiagnosticSnapshot().LastConnectionFailure;
        Ensure(protocol is not null, "protocol failure snapshot");
        Ensure(protocol!.Stage == SharpLinkConnectionFailureStage.Protocol, "protocol failure stage");
        Ensure(protocol.Classification == SharpLinkConnectionFailureClass.Protocol,
            "protocol failure classification");
        Ensure(!client.ExportDiagnosticSnapshotJson().Contains(protocolSecret, StringComparison.Ordinal),
            "protocol message redacted");
    }

    [Test]
    public async Task PendingNearCapacityShouldUseExistingOwnerCountsAndNeverExportMetadata()
    {
        const string metadataSecret = "metadata-secret-tenant-token";
        var transport = new TestClientTransportFactory(ProtocolV2Capabilities.Metadata);
        await using var client = ClientBuilderTestHelper.Build(transport, builder =>
            builder.UseProtocol(options => options.MaxPendingRequestsPerConnection = 2));
        await client.ConnectAsync();
        var connection = GetSingleFixedConnection(client);
        var metadata = new SharpLinkMetadata(
            new KeyValuePair<string, string>("authorization", metadataSecret));

        var invocation = ClientInvokerTestHelper.InvokeUnaryAsync(client, metadata).AsTask();
        var request = await transport.Connection.WaitForSentPacket(ProtocolV2FrameType.Request);
        var synthetic = connection.PendingCalls.Rent<int>(out var syntheticId);

        try
        {
            var snapshot = client.GetDiagnosticSnapshot();
            Ensure(snapshot.Resources.PendingRequests == 2, "aggregate pending count near capacity");
            Ensure(snapshot.Topology.Connections.Count == 1, "pending connection snapshot");
            var resources = snapshot.Topology.Connections[0].Resources;
            Ensure(resources.PendingRequests == 2, "connection pending count near capacity");
            Ensure(resources.PendingRequestCapacity == 2, "connection pending capacity");
            Ensure(!client.ExportDiagnosticSnapshotJson().Contains(metadataSecret, StringComparison.Ordinal),
                "active request metadata redacted");
        }
        finally
        {
            await transport.Connection.InjectInt32ResponseAsync(unchecked((long)request.RequestId));
            _ = await invocation;
            connection.PendingCalls.DispatchError(
                syntheticId,
                new InvalidOperationException("synthetic pending completion"));
            try
            {
                _ = await synthetic.AsValueTask();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    [Test]
    public async Task StaticConvergenceSnapshotShouldStaySafeWhileAnotherEndpointIsConnecting()
    {
        const string readySecret = "ready-secret.internal";
        const string connectingSecret = "connecting-secret.internal";
        var ready = new TestClientTransportFactory();
        var blocked = new BlockingConnectFactory(new TestClientTransportFactory());
        await using var client = ClientBuilderTestHelper.BuildStatic(
        [
            new StaticEndpointConfiguration(
                new SharpLinkEndpoint
                {
                    Id = "ready-secret-id",
                    Address = new SharpLinkTcpAddress(readySecret, 6101)
                },
                ready),
            new StaticEndpointConfiguration(
                new SharpLinkEndpoint
                {
                    Id = "connecting-secret-id",
                    Address = new SharpLinkTcpAddress(connectingSecret, 6102)
                },
                blocked)
        ],
        builder => builder.UseCluster(options =>
        {
            options.MinReadyEndpoints = 2;
            options.MaxConnections = 2;
            options.MaxConnectionsPerEndpoint = 1;
        }));

        var connect = client.ConnectAsync().AsTask();
        await blocked.Entered.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => client.GetDiagnosticSnapshot().Resources.ReadyConnections == 1,
            TimeSpan.FromSeconds(2));

        var snapshot = client.GetDiagnosticSnapshot();
        Ensure(snapshot.Topology.TotalEndpoints == 2, "multi-endpoint total during convergence");
        Ensure(snapshot.Resources.ReadyConnections == 1, "one ready endpoint during convergence");
        Ensure(snapshot.Readiness.TargetReadyEndpoints == 2, "convergence target retained");
        var json = client.ExportDiagnosticSnapshotJson();
        Ensure(!json.Contains(readySecret, StringComparison.Ordinal), "ready endpoint host redacted");
        Ensure(!json.Contains(connectingSecret, StringComparison.Ordinal), "connecting endpoint host redacted");
        Ensure(!json.Contains("ready-secret-id", StringComparison.Ordinal), "ready endpoint id redacted");
        Ensure(!json.Contains("connecting-secret-id", StringComparison.Ordinal), "connecting endpoint id redacted");

        blocked.Release();
        await connect.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task FixedDrainingConnectionShouldRemainSnapshotSafe()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);
        await client.ConnectAsync();
        var connection = GetSingleFixedConnection(client);
        connection.MarkDraining();

        var snapshot = client.GetDiagnosticSnapshot();
        Ensure(snapshot.Topology.Connections.Count == 1, "draining connection retained in snapshot");
        Ensure(snapshot.Topology.Connections[0].State == SharpLinkSupportConnectionState.Draining,
            "draining state captured");
        Ensure(!snapshot.Topology.Connections[0].CanAcceptCalls,
            "draining connection does not advertise call acceptance");
    }

    [Test]
    public async Task DynamicEndpointReplacementShouldRaceWithCaptureWithoutLeakingEndpointData()
    {
        const string firstSecret = "old-generation-secret.internal";
        const string secondSecret = "new-generation-secret.internal";
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(1,
        [
            DynamicEndpoint("old-secret-id", firstSecret, 6201)
        ]));
        var factories = new Dictionary<string, IClientTransportFactory>(StringComparer.Ordinal)
        {
            ["old-secret-id"] = new TestClientTransportFactory(),
            ["new-secret-id"] = new TestClientTransportFactory()
        };
        await using var client = ClientBuilderTestHelper.BuildDynamic(
            resolver,
            endpoint => factories[endpoint.Id]);
        await client.ConnectAsync();

        var capture = Task.Run(() =>
        {
            for (var index = 0; index < 128; index++)
            {
                var json = client.ExportDiagnosticSnapshotJson();
                Ensure(!json.Contains(firstSecret, StringComparison.Ordinal), "old dynamic host redacted");
                Ensure(!json.Contains(secondSecret, StringComparison.Ordinal), "new dynamic host redacted");
                Ensure(!json.Contains("old-secret-id", StringComparison.Ordinal), "old dynamic id redacted");
                Ensure(!json.Contains("new-secret-id", StringComparison.Ordinal), "new dynamic id redacted");
            }
        });

        resolver.Publish(new SharpLinkEndpointSnapshot(2,
        [
            DynamicEndpoint("new-secret-id", secondSecret, 6202)
        ]));
        await WaitUntilAsync(
            () => client.GetDiagnosticSnapshot().Topology.Endpoints.Any(endpoint => endpoint.Generation == 2),
            TimeSpan.FromSeconds(3));
        await capture.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task SnapshotAndStopRaceShouldNotDereferenceReleasedOwners()
    {
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.Build(transport);
        await client.ConnectAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var capture = Task.Run(async () =>
        {
            started.TrySetResult();
            for (var index = 0; index < 128; index++)
            {
                _ = client.GetDiagnosticSnapshot();
                if ((index & 7) == 0)
                    _ = client.ExportDiagnosticSnapshotJson();
                await Task.Yield();
            }
        });
        await started.Task;
        var stop = client.StopAsync().AsTask();
        await Task.WhenAll(capture, stop).WaitAsync(TimeSpan.FromSeconds(5));
        _ = client.GetDiagnosticSnapshot();
    }

    [Test]
    public async Task AuthenticatorCredentialAndEndpointSentinelsShouldNeverAppearInJson()
    {
        const string tokenSecret = "credential-token-secret-7f59f4";
        const string uriCredentialSecret = "user:password@private.example";
        var token = Encoding.UTF8.GetBytes(tokenSecret);
        var endpoint = new SharpLinkEndpoint
        {
            Id = "sentinel-endpoint-id",
            Address = new SharpLinkTcpAddress("private.example", 6301),
            Authority = uriCredentialSecret,
            Attributes = new Dictionary<string, string>
            {
                ["connection-string"] = "Server=secret;Password=business-secret-value"
            }
        };
        await using var client = ClientBuilderTestHelper.BuildEndpoint(
            endpoint,
            new TestClientTransportFactory(),
            builder => builder.UseAuthenticator(SharpLinkAuthenticator.CreateClient(
                _ => ValueTask.FromResult<ReadOnlyMemory<byte>>(token))));

        var snapshot = client.GetDiagnosticSnapshot();
        Ensure(snapshot.Configuration.AuthenticationConfigured, "authenticator presence retained as bool");
        var json = client.ExportDiagnosticSnapshotJson();
        Ensure(!json.Contains(tokenSecret, StringComparison.Ordinal), "auth token redacted");
        Ensure(!json.Contains(uriCredentialSecret, StringComparison.Ordinal), "URI credential redacted");
        Ensure(!json.Contains("business-secret-value", StringComparison.Ordinal), "connection string redacted");
        Ensure(!json.Contains("sentinel-endpoint-id", StringComparison.Ordinal), "endpoint sentinel redacted");
    }

    private static ClientConnection GetSingleFixedConnection(SharpLinkClient client)
    {
        var connectionsField = typeof(SharpLinkClient).GetField(
            "_connections",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("cannot find fixed connection owner");
        var connections = (HashSet<ClientConnection>)connectionsField.GetValue(client)!;
        return connections.Single();
    }

    private static SharpLinkEndpoint DynamicEndpoint(string id, string host, int port) => new()
    {
        Id = id,
        Address = new SharpLinkTcpAddress(host, port),
        Attributes = new Dictionary<string, string>
        {
            ["tenant"] = $"tenant-secret-for-{id}"
        }
    };

    private static async Task CaptureConnectFailureAsync(SharpLinkClient client)
    {
        try
        {
            await client.ConnectAsync();
            throw new InvalidOperationException("expected connection failure");
        }
        catch (Exception exception) when (exception is not InvalidOperationException { Message: "expected connection failure" })
        {
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!condition() && Stopwatch.GetTimestamp() < deadline)
            await Task.Delay(10);
        if (!condition())
            throw new TimeoutException("support snapshot did not reach the expected state");
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

    private sealed class SilentHandshakeTransportFactory : IClientTransportFactory
    {
        internal TestTransportConnection Connection { get; } = new();

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ITransportConnection>(Connection);

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    private sealed class BlockingConnectFactory(IClientTransportFactory inner) : IClientTransportFactory
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return await inner.ConnectAsync(cancellationToken);
        }

        internal void Release() => _release.TrySetResult();

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class ControllableResolver(SharpLinkEndpointSnapshot initial) : ISharpLinkEndpointResolver
    {
        private readonly Channel<SharpLinkEndpointSnapshot> _snapshots =
            Channel.CreateUnbounded<SharpLinkEndpointSnapshot>();

        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(initial);

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var snapshot in _snapshots.Reader.ReadAllAsync(cancellationToken))
                yield return snapshot;
        }

        internal void Publish(SharpLinkEndpointSnapshot snapshot)
            => _snapshots.Writer.TryWrite(snapshot);

        public ValueTask DisposeAsync()
        {
            _snapshots.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
