namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private const int MaximumRegistrationPreparations = 16;
    private static readonly AsyncLocal<RegistrationPreparation?> CurrentRegistrationPreparation = new();
    private int _registrationPreparations;
    private bool _registrationPreparationSealed;
    private TaskCompletionSource? _registrationPreparationsDrained;
    private readonly ServerDeclaredMethodFactsQuota _declaredFactsQuota;

    private void ThrowIfRegistrationCallbackReentry()
    {
        for (var current = CurrentRegistrationPreparation.Value; current is not null; current = current.Previous)
            if (current.IsActive && ReferenceEquals(current.Server, this))
                throw new InvalidOperationException("A registration preparation callback cannot mutate or stop its own server.");
    }

    private bool TryBeginRegistrationPreparation(out RegistrationPreparation? owner,
        out SharpLinkAssemblyRegistrationError? error)
    {
        ThrowIfRegistrationCallbackReentry();
        lock (_registryGate)
        {
            if (_registrationPreparationSealed || CurrentState is ServerState.Draining or ServerState.Stopped or ServerState.Faulted)
            {
                owner = null;
                error = new(SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                    "The server no longer accepts registration preparation.");
                return false;
            }
            if (_registrationPreparations == MaximumRegistrationPreparations)
            {
                owner = null;
                error = new(SharpLinkAssemblyRegistrationErrorCode.CapacityExceeded,
                    "The server registration preparation capacity is full.");
                return false;
            }
            if (_registrationPreparations == 0)
                _registrationPreparationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _registrationPreparations++;
            try
            {
                owner = new RegistrationPreparation(this, CurrentRegistrationPreparation.Value);
            }
            catch
            {
                if (--_registrationPreparations == 0)
                    _registrationPreparationsDrained!.TrySetResult();
                throw;
            }
        }
        CurrentRegistrationPreparation.Value = owner;
        error = null;
        return true;
    }

    // Called only under the existing registry publication gate at the Stop cut.
    private void SealRegistrationPreparationsLocked() => _registrationPreparationSealed = true;

    private Task CaptureRegistrationPreparationsDrain()
    {
        lock (_registryGate)
            return _registrationPreparationsDrained?.Task ?? Task.CompletedTask;
    }

    private sealed class RegistrationPreparation : IDisposable
    {
        internal SharpLinkServer Server { get; }
        internal RegistrationPreparation? Previous { get; }
        private readonly List<SharpLinkDynamicModule> _dependencies = [];
        private int _disposed;
        internal bool IsActive => Volatile.Read(ref _disposed) == 0;

        internal RegistrationPreparation(SharpLinkServer server, RegistrationPreparation? previous)
        {
            Server = server;
            Previous = previous;
        }

        // The caller holds Server._registryGate. No generated/user callbacks occur here.
        internal bool TryPinDependencies(SharpLinkDynamicModule[] modules)
        {
            if (_dependencies.Count != 0)
                throw new InvalidOperationException("A preparation cannot silently replay dependency capture.");
            foreach (var module in modules)
            {
                if (!module.TryAcquirePreparation())
                    return false;
                try { _dependencies.Add(module); }
                catch { module.ReleasePreparation(); throw; }
            }
            return true;
        }

        internal bool DependenciesAreRunning()
        {
            foreach (var module in _dependencies)
                if (module.State != SharpLinkDynamicModuleState.Running)
                    return false;
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            if (ReferenceEquals(CurrentRegistrationPreparation.Value, this))
                CurrentRegistrationPreparation.Value = Previous;
            List<Exception>? failures = null;
            try
            {
                foreach (var module in _dependencies)
                {
                    try { module.ReleasePreparation(); }
                    catch (Exception exception) { (failures ??= []).Add(exception); }
                }
                _dependencies.Clear();
            }
            finally
            {
                TaskCompletionSource? completed = null;
                lock (Server._registryGate)
                {
                    if (--Server._registrationPreparations < 0)
                        throw new InvalidOperationException("Registration preparation owner underflowed.");
                    if (Server._registrationPreparations == 0)
                        completed = Server._registrationPreparationsDrained;
                }
                completed?.TrySetResult();
            }
            if (failures is { Count: 1 })
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures is not null)
                throw new AggregateException(failures);
        }
    }
}
