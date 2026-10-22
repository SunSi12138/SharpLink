using System.Runtime.CompilerServices;

namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    // Value-type specialization keeps the immediate path free of queued-registration state and
    // runtime selection branches. Both entry points still share request/decode/resource handling.
    private interface IRequestDispatchSelection
    {
        ServerCallCancellationState? CallState { get; }
        ServerRetainedAdmissionPayload? RetainedPayload { get; }
        int ClientStreamCount { get; }
        SharpLinkErrorCode MissingServiceErrorCode { get; }
        string GetMissingServiceMessage(long interfaceHash);
        bool TryGetService(SharpLinkServer server, long interfaceHash, out ServiceRegistration service);
    }

    internal readonly struct ImmediateRequestDispatch : IRequestDispatchSelection
    {
        public ServerCallCancellationState? CallState => null;
        public ServerRetainedAdmissionPayload? RetainedPayload => null;
        public int ClientStreamCount => 0;
        public SharpLinkErrorCode MissingServiceErrorCode => SharpLinkErrorCode.Unimplemented;

        public string GetMissingServiceMessage(long interfaceHash)
            => $"Service {interfaceHash} is not implemented.";

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetService(SharpLinkServer server, long interfaceHash, out ServiceRegistration service)
            => Volatile.Read(ref server._services).TryGetValue(interfaceHash, out service!);
    }

    private readonly struct AdmittedRequestDispatch(
        QueuedServiceSelection serviceSelection,
        ServerCallCancellationState callState,
        ServerRetainedAdmissionPayload retainedPayload,
        int clientStreamCount = 0) : IRequestDispatchSelection
    {
        public ServerCallCancellationState CallState => callState;
        public ServerRetainedAdmissionPayload RetainedPayload => retainedPayload;
        public int ClientStreamCount => clientStreamCount;
        public SharpLinkErrorCode MissingServiceErrorCode => SharpLinkErrorCode.Unavailable;

        public string GetMissingServiceMessage(long interfaceHash) => "RPC module is draining";

        public bool TryGetService(SharpLinkServer server, long interfaceHash, out ServiceRegistration service)
            => serviceSelection.TryGetService(out service);
    }

    /// <summary>
    /// Captures identity, not lifetime, for an admission waiter. Dynamic registration roots remain
    /// owned by the registry until retirement. A waiter must not prevent collectible unload after
    /// ReferencesReleased, and must never resolve a different generation when it resumes.
    /// </summary>
    private readonly struct QueuedServiceSelection
    {
        private readonly ServiceRegistration? _staticService;
        private readonly WeakReference<ServiceRegistration>? _dynamicService;

        internal QueuedServiceSelection(ServiceRegistration service)
        {
            if (service.HasModule)
                _dynamicService = new WeakReference<ServiceRegistration>(service);
            else
                _staticService = service;
        }

        internal bool TryGetService(out ServiceRegistration service)
        {
            if (_staticService is { } staticService)
            {
                service = staticService;
                return true;
            }
            return _dynamicService!.TryGetTarget(out service!);
        }
    }
}
