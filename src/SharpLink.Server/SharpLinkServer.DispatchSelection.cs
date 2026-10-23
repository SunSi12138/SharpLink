namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    /// <summary>
    /// Captures identity, not lifetime, for an admission waiter. Dynamic registration roots remain
    /// owned by the registry until retirement. A waiter must not prevent collectible unload after
    /// ReferencesReleased, and must never resolve a different generation when it resumes.
    /// </summary>
    internal readonly struct QueuedServiceSelection
    {
        private readonly ServiceRegistration? _staticService;
        private readonly WeakReference<ServiceRegistration>? _dynamicService;

        internal QueuedServiceSelection(ServiceRegistration service)
        {
            if (service.HasModule)
                _dynamicService = service.QueuedWeakIdentity;
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
