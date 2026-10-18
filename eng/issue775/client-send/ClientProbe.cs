// Diagnostic-only partial class. Copied into an isolated checkout by client_send.py;
// never compiled by normal builds and never changes an existing production method.
namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    internal ClientSendProbe CreateClientSendProbe<T>(RpcMethodDescriptor method) where T : IService
        => new(this, method,
#if SHARPLINK_STATIC_BINDING_PROTOTYPE
            StaticBindingTestSite<T>(),
#endif
            Volatile.Read(ref _readyConnections));

    internal sealed class ClientSendProbe
    {
        private readonly SharpLinkClient _client;
        private readonly RpcMethodDescriptor _method;
        private readonly ClientConnection[] _connections;
#if SHARPLINK_STATIC_BINDING_PROTOTYPE
        private readonly StaticBindingCallSite _site;
#endif
        internal ClientSendProbe(SharpLinkClient client, RpcMethodDescriptor method,
#if SHARPLINK_STATIC_BINDING_PROTOTYPE
            StaticBindingCallSite site,
#endif
            ClientConnection[] connections)
        {
            _client = client;
            _method = method;
            _connections = connections;
#if SHARPLINK_STATIC_BINDING_PROTOTYPE
            _site = site;
#endif
        }

        // These loops expose existing private paths, NOT alternative RPC implementations.
        // The extra result validation is identical in both arms. Do not subtract their
        // costs from proxy timing: inlining and workload context differ.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void ControlLoop(int count)
        {
            for (var i = 0; i < count; i++)
            {
                var interceptors = _client.CaptureInterceptorGenerationForInvocation();
                var control = _client.ResolveCallControlForInvocation(
                    _method, null, includeClientDefault: false, interceptors);
#if SHARPLINK_STATIC_BINDING_PROTOTYPE
                control = control with { BindingSite = _site };
                if (!ReferenceEquals(control.BindingSite, _site))
                    throw new InvalidOperationException("Lost binding site.");
#endif
                if (control.Deadline.HasValue || control.Metadata is not null || control.LogicalCall is not null)
                    throw new InvalidOperationException("Unexpected timed/metadata control.");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void RouteLoop(int count)
        {
            for (var i = 0; i < count; i++)
            {
#if SHARPLINK_STATIC_BINDING_PROTOTYPE
                var target = _client.GetReadyStaticTarget(_site, _method, null);
                var connection = target.Connection;
#else
                var connection = _client.GetReadyConnection(_method, null, null);
#endif
                connection.ReleaseCallAdmissionReservation();
            }
        }

        internal int ControlBytes => Unsafe.SizeOf<ResolvedCallControl>();
        internal int Connections => _connections.Length;
        internal long QueuedBytes => _connections.Sum(c => c.Session.QueuedSendBytes);
        internal int Reservations => _connections.Sum(c => c.CallAdmissionReservationCount);
        internal int Active => _connections.Sum(c => c.ActiveCallCount);
        internal long Binds =>
#if SHARPLINK_STATIC_BINDING_PROTOTYPE
            _connections.Sum(c => c.StaticBindings.Sent);
#else
            0;
#endif
        internal async Task FlushAsync()
        {
            foreach (var connection in _connections)
                await connection.Session.FlushSendQueueAsync().ConfigureAwait(false);
        }
    }
}
