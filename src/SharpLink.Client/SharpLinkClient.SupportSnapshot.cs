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
        var failure = CaptureLastConnectionFailurePublication(capturedAt);
        var topologyCapture = CaptureSupportTopology(options, failure?.EndpointKey);
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

        var lastFailure = MaterializeLastConnectionFailure(
            failure,
            capturedAt,
            topologyCapture.FailureEndpointSafeId);
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
            topologyCapture.Topology,
            topologyCapture.Resources,
            lastFailure);
    }

    internal void RecordConnectionFailure(
        SharpLinkConnectionFailureStage stage,
        Exception exception,
        string? endpointSafeId)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (endpointSafeId is not null && !SupportRedaction.IsSafeEndpointId(endpointSafeId))
        {
            throw new ArgumentException(
                "Support failure endpoint identifiers must already be redacted ordinals.",
                nameof(endpointSafeId));
        }
        PublishConnectionFailure(stage, exception, endpointSafeId, endpointKey: null);
    }

    private void RecordClusterConnectionFailure(
        SharpLinkConnectionFailureStage stage,
        Exception exception,
        long? endpointKey)
    {
        ArgumentNullException.ThrowIfNull(exception);
        PublishConnectionFailure(stage, exception, directEndpointSafeId: null, endpointKey);
    }

    private void PublishConnectionFailure(
        SharpLinkConnectionFailureStage stage,
        Exception exception,
        string? directEndpointSafeId,
        long? endpointKey)
    {
        var primary = SupportRedaction.Unwrap(exception);
        Volatile.Write(
            ref _lastConnectionFailure,
            new ClientConnectionFailurePublication(
                SupportRedaction.ClassifyStage(stage, primary),
                SupportRedaction.ClassifyFailure(primary),
                primary is SharpLinkException sharpLink ? sharpLink.Code.ToString() : null,
                SupportRedaction.GetSafeExceptionType(primary),
                directEndpointSafeId,
                endpointKey,
                _runtimeContext.TimeProvider.GetUtcNow()));
    }

    private ClientConnectionFailurePublication? CaptureLastConnectionFailurePublication(DateTimeOffset capturedAt)
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
                null,
                capturedAt);
        }
        return failure;
    }

    private static SharpLinkConnectionFailureSnapshot? MaterializeLastConnectionFailure(
        ClientConnectionFailurePublication? failure,
        DateTimeOffset capturedAt,
        string? mappedEndpointSafeId)
    {
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
            failure.DirectEndpointSafeId ?? mappedEndpointSafeId,
            failure.OccurredAtUtc,
            age);
    }

    private SupportTopologyCapture CaptureSupportTopology(
        SharpLinkClientSupportSnapshotOptions options,
        long? failureEndpointKey)
        => _cluster switch
        {
            null => CaptureFixedSupportTopology(options),
            StaticClusterRuntime cluster => CaptureStaticClusterSupportTopology(
                cluster,
                options,
                failureEndpointKey),
            DynamicClusterRuntime cluster => CaptureDynamicClusterSupportTopology(
                cluster,
                options,
                failureEndpointKey),
            _ => throw new InvalidOperationException("Unsupported SharpLink cluster runtime.")
        };

    private SupportTopologyCapture CaptureFixedSupportTopology(SharpLinkClientSupportSnapshotOptions options)
    {
        lock (_poolGate)
        {
            const string endpointSafeId = "endpoint-0001";
            var capturedConnectionIndex = 0;
            var connections = CaptureOwnedConnections(
                _connections,
                endpointSafeId,
                options.MaxConnections,
                ref capturedConnectionIndex);
            var endpoint = new SharpLinkSupportEndpointSnapshot(
                endpointSafeId,
                SupportRedaction.GetTransportKind(_fixedEndpoint, transportFactory),
                _fixedEndpoint?.Authority is not null,
                connections.ReadyConnections != 0
                    ? SharpLinkSupportEndpointState.Ready
                    : SharpLinkSupportEndpointState.Unavailable,
                null,
                connections.ReadyConnections,
                connections.ActiveConnections,
                connections.RetiringConnections,
                State == SharpLinkConnectionState.Connecting ? 1 : 0);
            var topology = new SharpLinkSupportTopologySnapshot(
                SharpLinkSupportTopologyKind.Fixed,
                1,
                1,
                false,
                connections.TotalConnections,
                connections.Details.Length,
                connections.TotalConnections > connections.Details.Length,
                Array.AsReadOnly([endpoint]),
                Array.AsReadOnly(connections.Details));
            return new SupportTopologyCapture(
                topology,
                connections.ToResourceSnapshot(),
                endpointSafeId);
        }
    }

    private SupportTopologyCapture CaptureStaticClusterSupportTopology(
        StaticClusterRuntime cluster,
        SharpLinkClientSupportSnapshotOptions options,
        long? failureEndpointKey)
    {
        lock (cluster._gate)
        {
            var totalEndpoints = cluster._endpoints.Length;
            var capturedEndpointCount = Math.Min(totalEndpoints, options.MaxEndpoints);
            var endpointSnapshots = new SharpLinkSupportEndpointSnapshot[capturedEndpointCount];
            var connectionSnapshots = new List<SharpLinkSupportConnectionSnapshot>(
                Math.Min(options.MaxConnections, cluster._options.MaxConnections));
            var capturedConnectionIndex = 0;
            var totalConnections = 0;
            var readyConnections = 0;
            var pendingRequests = 0;
            var activeCalls = 0;
            var activeStreams = 0;
            long sendQueuedBytes = 0;
            string? failureEndpointSafeId = null;

            for (var index = 0; index < totalEndpoints; index++)
            {
                var endpoint = cluster._endpoints[index];
                var safeId = SupportRedaction.EndpointOrdinal(index);
                if (failureEndpointKey == endpoint.Index)
                    failureEndpointSafeId = safeId;
                var detailBudget = index < capturedEndpointCount
                    ? Math.Max(0, options.MaxConnections - capturedConnectionIndex)
                    : 0;
                var connections = CaptureOwnedConnections(
                    endpoint.Connections,
                    safeId,
                    detailBudget,
                    ref capturedConnectionIndex);
                totalConnections += connections.TotalConnections;
                readyConnections += connections.ReadyConnections;
                pendingRequests += connections.PendingRequests;
                activeCalls += connections.ActiveCalls;
                activeStreams += connections.ActiveStreams;
                sendQueuedBytes += connections.SendQueuedBytes;

                if (index >= capturedEndpointCount)
                    continue;
                endpointSnapshots[index] = new SharpLinkSupportEndpointSnapshot(
                    safeId,
                    SupportRedaction.GetTransportKind(
                        endpoint.Configuration.Endpoint,
                        endpoint.Configuration.TransportFactory),
                    endpoint.Configuration.Endpoint.Authority is not null,
                    connections.ReadyConnections != 0
                        ? SharpLinkSupportEndpointState.Ready
                        : SharpLinkSupportEndpointState.Unavailable,
                    null,
                    connections.ReadyConnections,
                    connections.ActiveConnections,
                    connections.RetiringConnections,
                    endpoint.ConnectingCount);
                connectionSnapshots.AddRange(connections.Details);
            }

            return new SupportTopologyCapture(
                new SharpLinkSupportTopologySnapshot(
                    SharpLinkSupportTopologyKind.Static,
                    totalEndpoints,
                    capturedEndpointCount,
                    totalEndpoints > capturedEndpointCount,
                    totalConnections,
                    connectionSnapshots.Count,
                    totalConnections > connectionSnapshots.Count,
                    Array.AsReadOnly(endpointSnapshots),
                    connectionSnapshots.AsReadOnly()),
                new SharpLinkSupportResourceSnapshot(
                    pendingRequests,
                    activeCalls,
                    activeStreams,
                    sendQueuedBytes,
                    readyConnections),
                failureEndpointSafeId);
        }
    }

    private SupportTopologyCapture CaptureDynamicClusterSupportTopology(
        DynamicClusterRuntime cluster,
        SharpLinkClientSupportSnapshotOptions options,
        long? failureEndpointKey)
    {
        lock (cluster._gate)
        {
            var states = cluster._current.States;
            var totalEndpoints = states.Count;
            var capturedEndpointCount = Math.Min(totalEndpoints, options.MaxEndpoints);
            var endpointSnapshots = new SharpLinkSupportEndpointSnapshot[capturedEndpointCount];
            var connectionSnapshots = new List<SharpLinkSupportConnectionSnapshot>(
                Math.Min(options.MaxConnections, cluster._options.MaxConnections));
            var capturedConnectionIndex = 0;
            var totalConnections = 0;
            var readyConnections = 0;
            var pendingRequests = 0;
            var activeCalls = 0;
            var activeStreams = 0;
            long sendQueuedBytes = 0;
            string? failureEndpointSafeId = null;

            for (var index = 0; index < totalEndpoints; index++)
            {
                var endpoint = states[index];
                var safeId = SupportRedaction.EndpointOrdinal(index);
                if (failureEndpointKey == endpoint.Generation)
                    failureEndpointSafeId = safeId;
                var detailBudget = index < capturedEndpointCount
                    ? Math.Max(0, options.MaxConnections - capturedConnectionIndex)
                    : 0;
                var connections = CaptureOwnedConnections(
                    cluster._connections.GetOwnedConnections(endpoint),
                    safeId,
                    detailBudget,
                    ref capturedConnectionIndex);
                totalConnections += connections.TotalConnections;
                readyConnections += connections.ReadyConnections;
                pendingRequests += connections.PendingRequests;
                activeCalls += connections.ActiveCalls;
                activeStreams += connections.ActiveStreams;
                sendQueuedBytes += connections.SendQueuedBytes;

                if (index >= capturedEndpointCount)
                    continue;
                endpointSnapshots[index] = new SharpLinkSupportEndpointSnapshot(
                    safeId,
                    SupportRedaction.GetTransportKind(
                        endpoint.Configuration.Endpoint,
                        endpoint.Configuration.TransportFactory),
                    endpoint.Configuration.Endpoint.Authority is not null,
                    endpoint.Retiring
                        ? SharpLinkSupportEndpointState.Retiring
                        : connections.ReadyConnections != 0
                            ? SharpLinkSupportEndpointState.Ready
                            : SharpLinkSupportEndpointState.Unavailable,
                    endpoint.Generation,
                    connections.ReadyConnections,
                    connections.ActiveConnections,
                    connections.RetiringConnections,
                    endpoint.ConnectingCount);
                connectionSnapshots.AddRange(connections.Details);
            }

            return new SupportTopologyCapture(
                new SharpLinkSupportTopologySnapshot(
                    SharpLinkSupportTopologyKind.Dynamic,
                    totalEndpoints,
                    capturedEndpointCount,
                    totalEndpoints > capturedEndpointCount,
                    totalConnections,
                    connectionSnapshots.Count,
                    totalConnections > connectionSnapshots.Count,
                    Array.AsReadOnly(endpointSnapshots),
                    connectionSnapshots.AsReadOnly()),
                new SharpLinkSupportResourceSnapshot(
                    pendingRequests,
                    activeCalls,
                    activeStreams,
                    sendQueuedBytes,
                    readyConnections),
                failureEndpointSafeId);
        }
    }

    private SupportConnectionCapture CaptureOwnedConnections(
        IEnumerable<ClientConnection> connections,
        string endpointSafeId,
        int maxConnectionDetails,
        ref int capturedConnectionIndex)
    {
        List<SharpLinkSupportConnectionSnapshot>? details = null;
        var totalConnections = 0;
        var readyConnections = 0;
        var activeConnections = 0;
        var retiringConnections = 0;
        var pendingRequests = 0;
        var activeCalls = 0;
        var activeStreams = 0;
        long sendQueuedBytes = 0;

        foreach (var connection in connections)
        {
            totalConnections++;
            var state = connection.State;
            var canAcceptCalls = connection.CanAcceptCalls;
            var connectionActiveCalls = connection.ActiveCallCount;
            var pending = connection.PendingCalls.ActiveCount;
            var session = connection.Session.CaptureSupportSnapshot();
            if (canAcceptCalls)
                readyConnections++;
            if (state == ClientConnectionState.Ready)
                activeConnections++;
            else if (state == ClientConnectionState.Draining)
                retiringConnections++;
            pendingRequests += pending;
            activeCalls += connectionActiveCalls;
            activeStreams += session.ActiveStreams;
            sendQueuedBytes += session.SendQueuedBytes;

            if ((details?.Count ?? 0) >= maxConnectionDetails)
                continue;
            details ??= [];
            details.Add(new SharpLinkSupportConnectionSnapshot(
                $"connection-{++capturedConnectionIndex:D4}",
                endpointSafeId,
                state switch
                {
                    ClientConnectionState.Ready => SharpLinkSupportConnectionState.Ready,
                    ClientConnectionState.Draining => SharpLinkSupportConnectionState.Draining,
                    _ => SharpLinkSupportConnectionState.Closed
                },
                canAcceptCalls,
                connectionActiveCalls,
                new SharpLinkSupportConnectionResourceSnapshot(
                    pending,
                    connection.PendingCalls.Capacity,
                    null,
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
                    session.CipherSuite)));
        }

        return new SupportConnectionCapture(
            totalConnections,
            readyConnections,
            activeConnections,
            retiringConnections,
            pendingRequests,
            activeCalls,
            activeStreams,
            sendQueuedBytes,
            details?.ToArray() ?? []);
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
        string? DirectEndpointSafeId,
        long? EndpointKey,
        DateTimeOffset OccurredAtUtc);

    private sealed record SupportTopologyCapture(
        SharpLinkSupportTopologySnapshot Topology,
        SharpLinkSupportResourceSnapshot Resources,
        string? FailureEndpointSafeId);

    private readonly record struct SupportConnectionCapture(
        int TotalConnections,
        int ReadyConnections,
        int ActiveConnections,
        int RetiringConnections,
        int PendingRequests,
        int ActiveCalls,
        int ActiveStreams,
        long SendQueuedBytes,
        SharpLinkSupportConnectionSnapshot[] Details)
    {
        internal SharpLinkSupportResourceSnapshot ToResourceSnapshot()
            => new(PendingRequests, ActiveCalls, ActiveStreams, SendQueuedBytes, ReadyConnections);
    }

    private static class SupportRedaction
    {
        internal static string EndpointOrdinal(int index) => $"endpoint-{index + 1:D4}";

        internal static bool IsSafeEndpointId(string value)
        {
            if (!value.StartsWith("endpoint-", StringComparison.Ordinal) || value.Length != 13)
                return false;
            for (var index = 9; index < value.Length; index++)
                if (value[index] is < '0' or > '9')
                    return false;
            return true;
        }

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
            if (stage is SharpLinkConnectionFailureStage.Resolve or SharpLinkConnectionFailureStage.Readiness)
                return stage;
            if (exception is AuthenticationException)
                return SharpLinkConnectionFailureStage.Tls;
            if (exception is SharpLinkException sharpLink)
            {
                return sharpLink.Code switch
                {
                    SharpLinkErrorCode.AuthenticationRejected or
                    SharpLinkErrorCode.AuthenticationExpired or
                    SharpLinkErrorCode.AuthorizationDenied or
                    SharpLinkErrorCode.PermissionDenied => SharpLinkConnectionFailureStage.Authentication,
                    SharpLinkErrorCode.ProtocolViolation or
                    SharpLinkErrorCode.Unimplemented => SharpLinkConnectionFailureStage.Protocol,
                    _ => SharpLinkConnectionFailureStage.Handshake
                };
            }
            if (stage != SharpLinkConnectionFailureStage.Unknown)
                return stage;
            if (exception is SocketException or IOException)
                return SharpLinkConnectionFailureStage.Dial;
            return SharpLinkConnectionFailureStage.Unknown;
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
                return sharpLink.Code switch
                {
                    SharpLinkErrorCode.DeadlineExceeded => SharpLinkConnectionFailureClass.Timeout,
                    SharpLinkErrorCode.Cancelled => SharpLinkConnectionFailureClass.Cancelled,
                    SharpLinkErrorCode.AuthenticationRejected or
                    SharpLinkErrorCode.AuthenticationExpired or
                    SharpLinkErrorCode.AuthorizationDenied or
                    SharpLinkErrorCode.PermissionDenied => SharpLinkConnectionFailureClass.Authentication,
                    SharpLinkErrorCode.ProtocolViolation => SharpLinkConnectionFailureClass.Protocol,
                    SharpLinkErrorCode.Unimplemented => SharpLinkConnectionFailureClass.Version,
                    SharpLinkErrorCode.ResourceExhausted => SharpLinkConnectionFailureClass.Resource,
                    SharpLinkErrorCode.Unavailable or
                    SharpLinkErrorCode.ConnectionClosed => SharpLinkConnectionFailureClass.Transport,
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
