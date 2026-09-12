using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

[NotInParallel]
public sealed class SharpLinkClientSupportSnapshotReviewTests
{
    [Test]
    public async Task StaticConvergenceShouldExposeConnectingOwnerStateAndTransport()
    {
        const string readyHost = "review-ready-secret.internal";
        const string connectingHost = "review-connecting-secret.internal";
        var ready = new TestClientTransportFactory();
        var blocked = new BlockingConnectFactory(new TestClientTransportFactory());
        await using var client = ClientBuilderTestHelper.BuildStatic(
        [
            new StaticEndpointConfiguration(
                Endpoint("review-ready-id", readyHost, 7101),
                ready),
            new StaticEndpointConfiguration(
                Endpoint("review-connecting-id", connectingHost, 7102),
                blocked)
        ],
        builder => builder.UseCluster(options =>
        {
            options.MinReadyEndpoints = 2;
            options.MaxConnections = 2;
            options.MaxConnectionsPerEndpoint = 1;
        }));

        try
        {
            var connect = client.ConnectAsync().AsTask();
            await blocked.Entered.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() =>
            {
                var snapshot = client.GetDiagnosticSnapshot();
                return snapshot.Resources.ReadyConnections == 1 &&
                       snapshot.Topology.Endpoints.Any(endpoint => endpoint.ConnectingConnections == 1);
            });

            var snapshot = client.GetDiagnosticSnapshot();
            Ensure(snapshot.Topology.TotalEndpoints == 2, "static endpoint total");
            Ensure(snapshot.Topology.TotalConnections == 1, "ready physical connection total");
            Ensure(snapshot.Resources.ReadyConnections == 1, "ready connection aggregate");
            Ensure(snapshot.Topology.Endpoints.Count(endpoint => endpoint.ConnectingConnections == 1) == 1,
                "one endpoint must expose the in-flight connection attempt");
            Ensure(snapshot.Topology.Endpoints.All(endpoint => endpoint.Transport == SharpLinkSupportTransportKind.Tcp),
                "endpoint transport categories must come from the endpoint owner");

            var json = client.ExportDiagnosticSnapshotJson();
            Ensure(!json.Contains(readyHost, StringComparison.Ordinal), "ready host redacted");
            Ensure(!json.Contains(connectingHost, StringComparison.Ordinal), "connecting host redacted");
            Ensure(!json.Contains("review-ready-id", StringComparison.Ordinal), "ready endpoint id redacted");
            Ensure(!json.Contains("review-connecting-id", StringComparison.Ordinal), "connecting endpoint id redacted");

            blocked.Release();
            await connect.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => client.GetDiagnosticSnapshot().Resources.ReadyConnections == 2);
        }
        finally
        {
            blocked.Release();
        }
    }

    [Test]
    public async Task DynamicReplacementShouldKeepRetiringConnectionInTopologyAndResources()
    {
        const string oldHost = "review-old-generation-secret.internal";
        const string newHost = "review-new-generation-secret.internal";
        var oldTransport = new TestClientTransportFactory();
        var newTransport = new TestClientTransportFactory();
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(1,
        [
            Endpoint("review-old-id", oldHost, 7201)
        ]));
        var transports = new Dictionary<string, IClientTransportFactory>(StringComparer.Ordinal)
        {
            ["review-old-id"] = oldTransport,
            ["review-new-id"] = newTransport
        };
        await using var client = ClientBuilderTestHelper.BuildDynamic(
            resolver,
            endpoint => transports[endpoint.Id]);
        await client.ConnectAsync();

        var invocation = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();
        var request = await oldTransport.Connection
            .WaitForSentPacket(ProtocolV2FrameType.Request)
            .WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            resolver.Publish(new SharpLinkEndpointSnapshot(2,
            [
                Endpoint("review-new-id", newHost, 7202)
            ]));
            await WaitUntilAsync(() =>
            {
                var snapshot = client.GetDiagnosticSnapshot();
                return snapshot.Topology.Endpoints.Any(endpoint =>
                           endpoint.Generation == 1 && endpoint.State == SharpLinkSupportEndpointState.Retiring) &&
                       snapshot.Topology.Endpoints.Any(endpoint =>
                           endpoint.Generation == 2 && endpoint.ReadyConnections == 1);
            }, TimeSpan.FromSeconds(3));

            var snapshot = client.GetDiagnosticSnapshot();
            var retiring = snapshot.Topology.Endpoints.Single(endpoint => endpoint.Generation == 1);
            Ensure(retiring.State == SharpLinkSupportEndpointState.Retiring, "old generation endpoint state");
            Ensure(retiring.RetiringConnections == 1, "old generation retiring connection count");
            Ensure(retiring.Transport == SharpLinkSupportTransportKind.Tcp, "retiring endpoint transport");
            Ensure(snapshot.Topology.TotalConnections == 2, "ready plus draining physical connections");
            Ensure(snapshot.Resources.PendingRequests == 1, "draining pending call remains in aggregate");
            Ensure(snapshot.Resources.ActiveCalls == 1, "draining active call remains in aggregate");
            Ensure(snapshot.Resources.ReadyConnections == 1, "only replacement connection is ready");
            var draining = snapshot.Topology.Connections.Single(connection =>
                connection.EndpointSafeId == retiring.SafeId);
            Ensure(draining.State == SharpLinkSupportConnectionState.Draining, "retiring physical connection state");
            Ensure(draining.Resources.PendingRequests == 1, "retiring physical connection pending count");

            var json = client.ExportDiagnosticSnapshotJson();
            Ensure(!json.Contains(oldHost, StringComparison.Ordinal), "old endpoint host redacted");
            Ensure(!json.Contains(newHost, StringComparison.Ordinal), "new endpoint host redacted");
            Ensure(!json.Contains("review-old-id", StringComparison.Ordinal), "old endpoint id redacted");
            Ensure(!json.Contains("review-new-id", StringComparison.Ordinal), "new endpoint id redacted");
        }
        finally
        {
            await oldTransport.Connection.InjectInt32ResponseAsync(unchecked((long)request.RequestId));
            Ensure(await invocation.WaitAsync(TimeSpan.FromSeconds(2)) == 0, "retiring call completes normally");
        }
    }

    [Test]
    public async Task StaticRealDialFailureShouldPublishMappedSafeEndpointReference()
    {
        const string firstSecret = "review-static-first-dial-secret";
        const string secondSecret = "review-static-second-dial-secret";
        await using var client = ClientBuilderTestHelper.BuildStatic(
        [
            new StaticEndpointConfiguration(
                Endpoint("review-static-first-id", "review-static-first.internal", 7301),
                new FailingTransportFactory(new IOException(firstSecret))),
            new StaticEndpointConfiguration(
                Endpoint("review-static-second-id", "review-static-second.internal", 7302),
                new FailingTransportFactory(new IOException(secondSecret)))
        ],
        builder => builder.UseCluster(options =>
        {
            options.MinReadyEndpoints = 1;
            options.MaxConnections = 2;
            options.MaxConnectionsPerEndpoint = 1;
        }));

        await CaptureConnectFailureAsync(client);
        var failure = client.GetDiagnosticSnapshot().LastConnectionFailure;
        Ensure(failure is not null, "static cluster last failure");
        Ensure(failure!.Stage == SharpLinkConnectionFailureStage.Dial, "static cluster dial stage");
        Ensure(failure.Classification == SharpLinkConnectionFailureClass.Transport,
            "static cluster transport classification");
        Ensure(failure.EndpointSafeId is "endpoint-0001" or "endpoint-0002",
            "static cluster endpoint failure maps to snapshot ordinal");

        var json = client.ExportDiagnosticSnapshotJson();
        Ensure(!json.Contains(firstSecret, StringComparison.Ordinal), "first exception text redacted");
        Ensure(!json.Contains(secondSecret, StringComparison.Ordinal), "second exception text redacted");
        Ensure(!json.Contains("review-static-first-id", StringComparison.Ordinal), "first raw endpoint id redacted");
        Ensure(!json.Contains("review-static-second-id", StringComparison.Ordinal), "second raw endpoint id redacted");
    }

    [Test]
    public async Task DynamicRealDialFailureShouldPublishMappedSafeEndpointReference()
    {
        const string failureSecret = "review-dynamic-dial-secret";
        const string endpointId = "review-dynamic-failing-id";
        var resolver = new ControllableResolver(new SharpLinkEndpointSnapshot(1,
        [
            Endpoint(endpointId, "review-dynamic-failing.internal", 7401)
        ]));
        await using var client = ClientBuilderTestHelper.BuildDynamic(
            resolver,
            _ => new FailingTransportFactory(new IOException(failureSecret)));

        await CaptureConnectFailureAsync(client);
        var failure = client.GetDiagnosticSnapshot().LastConnectionFailure;
        Ensure(failure is not null, "dynamic cluster last failure");
        Ensure(failure!.Stage == SharpLinkConnectionFailureStage.Dial, "dynamic cluster dial stage");
        Ensure(failure.Classification == SharpLinkConnectionFailureClass.Transport,
            "dynamic cluster transport classification");
        Ensure(failure.EndpointSafeId == "endpoint-0001",
            "dynamic cluster generation maps to the snapshot ordinal");

        var json = client.ExportDiagnosticSnapshotJson();
        Ensure(!json.Contains(failureSecret, StringComparison.Ordinal), "dynamic exception text redacted");
        Ensure(!json.Contains(endpointId, StringComparison.Ordinal), "dynamic raw endpoint id redacted");
        Ensure(!json.Contains("review-dynamic-failing.internal", StringComparison.Ordinal),
            "dynamic raw endpoint host redacted");
    }

    [Test]
    public async Task DynamicResolverFailureShouldPublishResolveStageWithoutRawText()
    {
        const string resolverSecret = "review-resolver-secret-topology";
        await using var client = ClientBuilderTestHelper.BuildDynamic(
            new FailingResolver(new IOException(resolverSecret)),
            _ => new FailingTransportFactory(new InvalidOperationException("unused transport")));

        await CaptureConnectFailureAsync(client);
        var failure = client.GetDiagnosticSnapshot().LastConnectionFailure;
        Ensure(failure is not null, "resolver last failure");
        Ensure(failure!.Stage == SharpLinkConnectionFailureStage.Resolve, "resolver failure stage");
        Ensure(failure.EndpointSafeId is null, "resolver failure has no fabricated endpoint identity");
        Ensure(!client.ExportDiagnosticSnapshotJson().Contains(resolverSecret, StringComparison.Ordinal),
            "resolver exception text redacted");
    }

    private static SharpLinkEndpoint Endpoint(string id, string host, int port) => new()
    {
        Id = id,
        Address = new SharpLinkTcpAddress(host, port)
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

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(2);
        var deadline = Stopwatch.GetTimestamp() + (long)(limit.TotalSeconds * Stopwatch.Frequency);
        while (!condition() && Stopwatch.GetTimestamp() < deadline)
            await Task.Delay(10);
        Ensure(condition(), "support snapshot did not reach the expected state");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
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

    private sealed class FailingTransportFactory(Exception failure) : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(failure);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

    private sealed class FailingResolver(Exception failure) : ISharpLinkEndpointResolver
    {
        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromException<SharpLinkEndpointSnapshot>(failure);

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
