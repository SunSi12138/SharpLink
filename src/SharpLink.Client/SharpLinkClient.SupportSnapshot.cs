using System.Runtime.InteropServices;
using System.Security.Authentication;

namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private const int SupportSnapshotSchemaVersion = 1;
    private ClientConnectionFailurePublication? _lastConnectionFailure;

    internal SharpLinkClientSupportSnapshot CaptureSupportSnapshot(
        SharpLinkClientSupportSnapshotOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var capturedAt = _runtimeContext.TimeProvider.GetUtcNow();
        var topologyCapture = CaptureSupportTopology();
        var topology = MaterializeSupportTopology(topologyCapture, options);
        var requestCompression = _requestCompressionPolicy.Current;
        var configuration = new SharpLinkSupportConfigurationSnapshot(
            GetRequestTimeoutPolicySnapshot(),
            GetHeartbeatConfigurationSnapshot(),
            GetReconnectPolicy(),
            GetRetryPolicySnapshot(),
            GetEndpointAdmissionPolicySnapshot(),
            GetCircuitBreakerPolicySnapshot(),
            _cluster is null ? null : _cluster.GetEndpointSelectionPolicySnapshot(),
            _protocolOptions.HandshakeTimeout,
            _protocolOptions.MaxPendingRequestsPerConnection,
            _protocolOptions.MaxConcurrentStreamsPerConnection,
            _runtimeContext.FlowControl.MaxSendQueueBytes,
            _connectionPoolOptions.MinConnections,
            _connectionPoolOptions.MaxConnections,
            _authenticator is not null,
            new SharpLinkSupportCompressionPolicySnapshot(
                requestCompression.Enabled,
                requestCompression.MinimumPayloadBytes,
                requestCompression.MinimumSavingsBytes,
                requestCompression.MinimumSavingsRatio),
            CaptureResponseCompressionPreference().Allowed);

        var resources = CaptureAggregateResources(topologyCapture);
        var lastFailure = CaptureLastConnectionFailure(capturedAt);
        var assemblyVersion = typeof(SharpLinkClient).Assembly.GetName().Version?.ToString() ?? "unknown";
        return new SharpLinkClientSupportSnapshot(
            SupportSnapshotSchemaVersion,
            capturedAt,
            new SharpLinkSupportRuntimeSnapshot(
                assemblyVersion,
                RuntimeInformation.FrameworkDescription,
                CaptureOperatingSystem(),
                RuntimeInformation.ProcessArchitecture.ToString(),
                "v2",
                _runtimeContext.PerformanceProfile),
            configuration,
            GetReadinessSnapshot(),
            topology,
            resources,
            lastFailure);
    }

    internal void RecordConnectionFailure(
        SharpLinkConnectionFailureStage stage,
        Exception exception,
        string? endpointSafeId)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var primary = SupportRedaction.Unwrap(exception);
        Volatile.Write(
            ref _lastConnectionFailure,
            new ClientConnectionFailurePublication(
                SupportRedaction.ClassifyStage(stage, primary),
                SupportRedaction.ClassifyFailure(primary),
                primary is SharpLinkException sharpLink ? sharpLink.Code.ToString() : null,
                SupportRedaction.GetSafeExceptionType(primary),
                endpointSafeId,
                _runtimeContext.TimeProvider.GetUtcNow()));
    }

    private SharpLinkConnectionFailureSnapshot? CaptureLastConnectionFailure(DateTimeOffset capturedAt)
    {
        var failure = Volatile.Read(ref _lastConnectionFailure);
        if (failure is null && _cluster is null && _connectTask is { IsFaulted: true, Exception: { } exception })
        {
            var primary = SupportRedaction.Unwrap(exception);
            failure = new ClientConnectionFailurePublication(
                SupportRedaction.ClassifyStage(SharpLinkConnectionFailureStage.Unknown, primary),
                SupportRedaction.ClassifyFailure(primary),
                primary is SharpLinkException sharpLink ? sharpLink.Code.ToString() : null,
                SupportRedaction.GetSafeExceptionType(primary),
                "endpoint-0001",
                capturedAt);
        }
        if (failure is null)
            return null;

        var age = capturedAt - failure.OccurredAtUtc;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        return new SharpLinkConnectionFailureSnapshot(
            failure.Stage,
            failure.Classification,
            failure.ErrorCode,
            failure.ExceptionType,
            failure.EndpointSafeId,
            failure.OccurredAtUtc,
            age);
    }

    private SupportTopologyCapture CaptureSupportTopology()
        => _cluster is null ? CaptureFixedSupportTopology() : CaptureClusterSupportTopology();

    private SupportTopologyCapture CaptureFixedSupportTopology()
    {
        ClientConnection[] connections;
        lock (_poolGate)
            connections = _connections.Count == 0 ? [] : _connections.ToArray();

        return new SupportTopologyCapture(
            SharpLinkSupportTopologyKind.Fixed,
            [new SupportEndpointCapture(
                "endpoint-0001",
                SupportRedaction.GetTransportKind(_fixedEndpoint, transportFactory),
                _fixedEndpoint?.Authority is not null,
                null,
                false,
                0,
                connections)]);
    }

    private SupportTopologyCapture CaptureClusterSupportTopology()
    {
        var readiness = GetReadinessSnapshot();
        var readyConnections = _cluster!.CaptureReadyConnections();
        var kind = _cluster is DynamicClusterRuntime
            ? SharpLinkSupportTopologyKind.Dynamic
            : SharpLinkSupportTopologyKind.Static;
        var groups = new List<ClusterConnectionGroup>();
        for (var index = 0; index < readyConnections.Length; index++)
        {
            var connection = readyConnections[index];
            var groupIndex = FindGroup(groups, connection.EndpointId, connection.EndpointGeneration);
            if (groupIndex < 0)
            {
                groups.Add(new ClusterConnectionGroup(
                    connection.EndpointId,
                    connection.EndpointGeneration,
                    [connection]));
            }
            else
            {
                groups[groupIndex].Connections.Add(connection);
            }
        }

        var totalEndpoints = Math.Max(readiness.ActiveEndpoints, groups.Count);
        var captures = new SupportEndpointCapture[totalEndpoints];
        var next = 0;
        for (; next < groups.Count; next++)
        {
            var group = groups[next];
            captures[next] = new SupportEndpointCapture(
                SupportRedaction.EndpointOrdinal(next),
                SharpLinkSupportTransportKind.Custom,
                false,
                kind == SharpLinkSupportTopologyKind.Dynamic ? group.Generation : null,
                false,
                0,
                group.Connections.ToArray());
        }
        for (; next < captures.Length; next++)
        {
            captures[next] = new SupportEndpointCapture(
                SupportRedaction.EndpointOrdinal(next),
                SharpLinkSupportTransportKind.Custom,
                false,
                null,
                false,
                0,
                []);
        }
        return new SupportTopologyCapture(kind, captures);
    }

    private static int FindGroup(
        List<ClusterConnectionGroup> groups,
        string? endpointId,
        long generation)
    {
        for (var index = 0; index < groups.Count; index++)
        {
            var candidate = groups[index];
            if (candidate.Generation == generation &&
                string.Equals(candidate.EndpointId, endpointId, StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }

    private SharpLinkSupportTopologySnapshot MaterializeSupportTopology(
        SupportTopologyCapture capture,
        SharpLinkClientSupportSnapshotOptions options)
    {
        var totalEndpoints = capture.Endpoints.Length;
        var capturedEndpointCount = Math.Min(totalEndpoints, options.MaxEndpoints);
        var endpointSnapshots = new SharpLinkSupportEndpointSnapshot[capturedEndpointCount];
        var totalConnections = 0;
        for (var index = 0; index < capture.Endpoints.Length; index++)
            totalConnections += capture.Endpoints[index].Connections.Length;

        var connectionSnapshots = new List<SharpLinkSupportConnectionSnapshot>(
            Math.Min(totalConnections, options.MaxConnections));
        for (var endpointIndex = 0; endpointIndex < capturedEndpointCount; endpointIndex++)
        {
            var endpoint = capture.Endpoints[endpointIndex];
            var ready = 0;
            var active = 0;
            var retiring = 0;
            for (var connectionIndex = 0; connectionIndex < endpoint.Connections.Length; connectionIndex++)
            {
                var state = endpoint.Connections[connectionIndex].State;
                if (state == ClientConnectionState.Ready)
                    active++;
                else if (state == ClientConnectionState.Draining)
                    retiring++;
                if (endpoint.Connections[connectionIndex].CanAcceptCalls)
                    ready++;
            }

            endpointSnapshots[endpointIndex] = new SharpLinkSupportEndpointSnapshot(
                endpoint.SafeId,
                endpoint.Transport,
                endpoint.AuthorityConfigured,
                endpoint.Retiring
                    ? SharpLinkSupportEndpointState.Retiring
                    : ready != 0
                        ? SharpLinkSupportEndpointState.Ready
                        : SharpLinkSupportEndpointState.Unavailable,
                endpoint.Generation,
                ready,
                active,
                retiring,
                endpoint.ConnectingConnections);

            for (var connectionIndex = 0;
                 connectionIndex < endpoint.Connections.Length && connectionSnapshots.Count < options.MaxConnections;
                 connectionIndex++)
            {
                connectionSnapshots.Add(CaptureConnectionSnapshot(
                    endpoint.Connections[connectionIndex],
                    endpoint.SafeId,
                    connectionSnapshots.Count));
            }
        }

        return new SharpLinkSupportTopologySnapshot(
            capture.Kind,
            totalEndpoints,
            capturedEndpointCount,
            totalEndpoints > capturedEndpointCount,
            totalConnections,
            connectionSnapshots.Count,
            totalConnections > connectionSnapshots.Count,
            Array.AsReadOnly(endpointSnapshots),
            connectionSnapshots.AsReadOnly());
    }

    private SharpLinkSupportConnectionSnapshot CaptureConnectionSnapshot(
        ClientConnection connection,
        string endpointSafeId,
        int index)
    {
        var session = connection.Session.CaptureSupportSnapshot();
        return new SharpLinkSupportConnectionSnapshot(
            $"connection-{index + 1:D4}",
            endpointSafeId,
            connection.State switch
            {
                ClientConnectionState.Ready => SharpLinkSupportConnectionState.Ready,
                ClientConnectionState.Draining => SharpLinkSupportConnectionState.Draining,
                _ => SharpLinkSupportConnectionState.Closed
            },
            connection.CanAcceptCalls,
            connection.ActiveCallCount,
            new SharpLinkSupportConnectionResourceSnapshot(
                connection.PendingCalls.ActiveCount,
                connection.PendingCalls.Capacity,
                0,
                session.SendQueuedBytes,
                session.SendQueueLimitBytes,
                session.ActiveStreams,
                _protocolOptions.MaxConcurrentStreamsPerConnection),
            new SharpLinkSupportNegotiationSnapshot(
                session.ProtocolPhase.ToString(),
                2,
                session.ProtocolMinorVersion,
                session.Capabilities?.ToString(),
                session.CompressionNegotiated,
                session.MaxFramePayloadBytes,
                session.StreamReceiveWindowBytes,
                session.ConnectionReceiveWindowBytes,
                session.Tls,
                session.TlsProtocol,
                session.CipherSuite));
    }

    private SharpLinkSupportResourceSnapshot CaptureAggregateResources(SupportTopologyCapture capture)
    {
        var pending = 0;
        var activeCalls = 0;
        var activeStreams = 0;
        long queuedBytes = 0;
        var readyConnections = 0;
        for (var endpointIndex = 0; endpointIndex < capture.Endpoints.Length; endpointIndex++)
        {
            var connections = capture.Endpoints[endpointIndex].Connections;
            for (var connectionIndex = 0; connectionIndex < connections.Length; connectionIndex++)
            {
                var connection = connections[connectionIndex];
                pending += connection.PendingCalls.ActiveCount;
                activeCalls += connection.ActiveCallCount;
                var session = connection.Session.CaptureSupportSnapshot();
                activeStreams += session.ActiveStreams;
                queuedBytes += session.SendQueuedBytes;
                if (connection.CanAcceptCalls)
                    readyConnections++;
            }
        }
        return new SharpLinkSupportResourceSnapshot(
            pending,
            activeCalls,
            activeStreams,
            queuedBytes,
            readyConnections);
    }

    private static string CaptureOperatingSystem()
    {
        if (OperatingSystem.IsWindows())
            return "windows";
        if (OperatingSystem.IsLinux())
            return "linux";
        if (OperatingSystem.IsMacOS())
            return "macos";
        if (OperatingSystem.IsFreeBSD())
            return "freebsd";
        return "other";
    }

    private sealed record ClientConnectionFailurePublication(
        SharpLinkConnectionFailureStage Stage,
        SharpLinkConnectionFailureClass Classification,
        string? ErrorCode,
        string ExceptionType,
        string? EndpointSafeId,
        DateTimeOffset OccurredAtUtc);

    private sealed record SupportTopologyCapture(
        SharpLinkSupportTopologyKind Kind,
        SupportEndpointCapture[] Endpoints);

    private sealed record SupportEndpointCapture(
        string SafeId,
        SharpLinkSupportTransportKind Transport,
        bool AuthorityConfigured,
        long? Generation,
        bool Retiring,
        int ConnectingConnections,
        ClientConnection[] Connections);

    private sealed record ClusterConnectionGroup(
        string? EndpointId,
        long Generation,
        List<ClientConnection> Connections);

    private static class SupportRedaction
    {
        internal static string EndpointOrdinal(int index) => $"endpoint-{index + 1:D4}";

        internal static Exception Unwrap(Exception exception)
        {
            while (exception is AggregateException { InnerExceptions.Count: 1 } aggregate)
                exception = aggregate.InnerExceptions[0];
            return exception;
        }

        internal static SharpLinkConnectionFailureStage ClassifyStage(
            SharpLinkConnectionFailureStage stage,
            Exception exception)
        {
            if (exception is AuthenticationException)
                return SharpLinkConnectionFailureStage.Tls;
            if (exception is SocketException or IOException)
                return SharpLinkConnectionFailureStage.Dial;
            if (exception is SharpLinkException sharpLink)
            {
                return sharpLink.Code.ToString() switch
                {
                    "AuthenticationRejected" or "AuthenticationExpired" or
                    "AuthorizationDenied" or "PermissionDenied" => SharpLinkConnectionFailureStage.Authentication,
                    "ProtocolViolation" or "VersionMismatch" => SharpLinkConnectionFailureStage.Protocol,
                    _ => stage == SharpLinkConnectionFailureStage.Unknown
                        ? SharpLinkConnectionFailureStage.Handshake
                        : stage
                };
            }
            return stage;
        }

        internal static SharpLinkConnectionFailureClass ClassifyFailure(Exception exception)
        {
            if (exception is OperationCanceledException)
                return SharpLinkConnectionFailureClass.Cancelled;
            if (exception is TimeoutException)
                return SharpLinkConnectionFailureClass.Timeout;
            if (exception is AuthenticationException)
                return SharpLinkConnectionFailureClass.Authentication;
            if (exception is SocketException socket && socket.SocketErrorCode == SocketError.ConnectionRefused)
                return SharpLinkConnectionFailureClass.Refused;
            if (exception is SharpLinkException sharpLink)
            {
                return sharpLink.Code.ToString() switch
                {
                    "DeadlineExceeded" => SharpLinkConnectionFailureClass.Timeout,
                    "AuthenticationRejected" or "AuthenticationExpired" or
                    "AuthorizationDenied" or "PermissionDenied" => SharpLinkConnectionFailureClass.Authentication,
                    "ProtocolViolation" => SharpLinkConnectionFailureClass.Protocol,
                    "VersionMismatch" => SharpLinkConnectionFailureClass.Version,
                    "ResourceExhausted" => SharpLinkConnectionFailureClass.Resource,
                    "Unavailable" or "ConnectionClosed" => SharpLinkConnectionFailureClass.Transport,
                    _ => SharpLinkConnectionFailureClass.Internal
                };
            }
            return exception is IOException or SocketException
                ? SharpLinkConnectionFailureClass.Transport
                : SharpLinkConnectionFailureClass.Internal;
        }

        internal static string GetSafeExceptionType(Exception exception)
            => exception switch
            {
                SharpLinkException => nameof(SharpLinkException),
                AuthenticationException => nameof(AuthenticationException),
                SocketException => nameof(SocketException),
                TimeoutException => nameof(TimeoutException),
                OperationCanceledException => nameof(OperationCanceledException),
                IOException => nameof(IOException),
                _ => nameof(Exception)
            };

        internal static SharpLinkSupportTransportKind GetTransportKind(
            SharpLinkEndpoint? endpoint,
            IClientTransportFactory factory)
        {
            if (endpoint is not null)
            {
                return endpoint.Address switch
                {
                    SharpLinkTcpAddress => SharpLinkSupportTransportKind.Tcp,
                    SharpLinkUnixDomainSocketAddress => SharpLinkSupportTransportKind.UnixDomainSocket,
                    SharpLinkNamedPipeAddress => SharpLinkSupportTransportKind.NamedPipe,
                    SharpLinkAnonymousPipeAddress => SharpLinkSupportTransportKind.AnonymousPipe,
                    SharpLinkSharedMemoryAddress => SharpLinkSupportTransportKind.SharedMemory,
                    _ => SharpLinkSupportTransportKind.Custom
                };
            }

            return factory.GetType().Name switch
            {
                "SocketClientTransportFactory" => SharpLinkSupportTransportKind.Tcp,
                "NamedPipeClientTransportFactory" => SharpLinkSupportTransportKind.NamedPipe,
                "AnonymousPipeClientTransportFactory" => SharpLinkSupportTransportKind.AnonymousPipe,
                "SharedMemoryClientTransportFactory" => SharpLinkSupportTransportKind.SharedMemory,
                _ => SharpLinkSupportTransportKind.Custom
            };
        }
    }
}
