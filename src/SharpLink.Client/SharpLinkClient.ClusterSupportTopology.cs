using EndpointState = SharpLink.Client.StaticClientRuntimeEndpointState;

namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private sealed partial class StaticClusterRuntime
    {
        public ClientConnection[] CaptureReadyConnections()
        {
            lock (_gate)
            {
                var ready = new List<ClientConnection>();
                for (var index = 0; index < _endpoints.Length; index++)
                {
                    foreach (var connection in _endpoints[index].Connections)
                    {
                        if (connection.CanAcceptCalls)
                            ready.Add(connection);
                    }
                }
                return ready.Count == 0 ? [] : ready.ToArray();
            }
        }

        public SupportTopologyCapture CaptureSupportTopology(
            SharpLinkClientSupportSnapshotOptions options,
            long? failureEndpointKey)
        {
            ArgumentNullException.ThrowIfNull(options);
            lock (_gate)
            {
                var totalEndpoints = _endpoints.Length;
                var capturedEndpointCount = Math.Min(totalEndpoints, options.MaxEndpoints);
                var endpointSnapshots = new SharpLinkSupportEndpointSnapshot[capturedEndpointCount];
                var connectionSnapshots = new List<SharpLinkSupportConnectionSnapshot>(
                    Math.Min(options.MaxConnections, _options.MaxConnections));
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
                    var endpoint = _endpoints[index];
                    var safeId = SupportRedaction.EndpointOrdinal(index);
                    if (failureEndpointKey == endpoint.Index)
                        failureEndpointSafeId = safeId;
                    var detailBudget = index < capturedEndpointCount
                        ? Math.Max(0, options.MaxConnections - capturedConnectionIndex)
                        : 0;
                    var connections = _client.CaptureOwnedConnections(
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
    }

    private sealed partial class DynamicClusterRuntime
    {
        public SupportTopologyCapture CaptureSupportTopology(
            SharpLinkClientSupportSnapshotOptions options,
            long? failureEndpointKey)
        {
            ArgumentNullException.ThrowIfNull(options);
            lock (_gate)
            {
                var states = _current.States;
                var totalEndpoints = states.Count;
                var capturedEndpointCount = Math.Min(totalEndpoints, options.MaxEndpoints);
                var endpointSnapshots = new SharpLinkSupportEndpointSnapshot[capturedEndpointCount];
                var connectionSnapshots = new List<SharpLinkSupportConnectionSnapshot>(
                    Math.Min(options.MaxConnections, _options.MaxConnections));
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
                    var connections = _client.CaptureOwnedConnections(
                        _connections.GetOwnedConnections(endpoint),
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
    }
}
