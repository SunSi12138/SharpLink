namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    /// <summary>
    /// Transitional production owner for one accepted call-capacity slot.
    ///
    /// The backing local/global accounting intentionally remains the existing
    /// Stop/Drain-hardened call accounting in this slice: reserving the permit
    /// consumes both capacity slots immediately, so a Reserved permit is still
    /// visible to drain as occupied work. Activation is therefore an ownership
    /// phase transition only; a later #273 slice can move decode between Reserve
    /// and Activate without first reopening the local-to-global drain race.
    /// </summary>
    internal ServerCallAdmissionResult TryReserveCall(
        ServerConnectionState connection,
        out ServerRequestPermit? permit)
        => TryReserveCall(connection, testHooks: null, out permit);

    internal ServerCallAdmissionResult TryReserveCall(
        ServerConnectionState connection,
        ServerRequestPermitTestHooks? testHooks,
        out ServerRequestPermit? permit)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var admission = TryAcquireCall(connection);
        if (admission != ServerCallAdmissionResult.Acquired)
        {
            permit = null;
            return admission;
        }

        try
        {
            permit = new ServerRequestPermit(this, connection, testHooks);
            return ServerCallAdmissionResult.Acquired;
        }
        catch
        {
            // The existing accounting is already capacity-owning at this point.
            // If permit materialization fails, roll both slots back synchronously.
            ReleaseCall(connection);
            throw;
        }
    }

    internal sealed class ServerRequestPermit : IDisposable
    {
        private const int Reserved = 0;
        private const int Activating = 1;
        private const int Active = 2;
        private const int Releasing = 3;
        private const int Disposed = 4;

        private readonly SharpLinkServer _server;
        private readonly ServerConnectionState _connection;
        private readonly ServerRequestPermitTestHooks? _testHooks;
        private int _state = Reserved;

        internal ServerRequestPermit(
            SharpLinkServer server,
            ServerConnectionState connection,
            ServerRequestPermitTestHooks? testHooks)
        {
            _server = server;
            _connection = connection;
            _testHooks = testHooks;
        }

        internal bool IsReserved => Volatile.Read(ref _state) == Reserved;

        internal bool IsActive => Volatile.Read(ref _state) == Active;

        internal void Activate()
        {
            var observed = Interlocked.CompareExchange(ref _state, Activating, Reserved);
            if (observed != Reserved)
            {
                if (observed is Releasing or Disposed)
                    throw new ObjectDisposedException(nameof(ServerRequestPermit));
                throw new InvalidOperationException("Only a reserved call permit can be activated.");
            }

            // Capacity was deliberately acquired during TryReserveCall. There is
            // no counter transfer here yet: this slice introduces the unique owner
            // while preserving the existing Stop/Drain linearization unchanged.
            Volatile.Write(ref _state, Active);
        }

        public void Dispose()
        {
            var spinner = new SpinWait();
            while (true)
            {
                var observed = Volatile.Read(ref _state);
                switch (observed)
                {
                    case Reserved:
                        if (Interlocked.CompareExchange(ref _state, Releasing, Reserved) != Reserved)
                            continue;
                        ReleaseBackingCapacity();
                        return;
                    case Activating:
                        spinner.SpinOnce();
                        continue;
                    case Active:
                        if (Interlocked.CompareExchange(ref _state, Releasing, Active) != Active)
                            continue;
                        ReleaseBackingCapacity();
                        return;
                    case Releasing:
                        _testHooks?.DisposeObservedReleasing?.Invoke();
                        spinner.SpinOnce();
                        continue;
                    case Disposed:
                        return;
                    default:
                        throw new InvalidOperationException("Unknown server request permit state.");
                }
            }
        }

        private void ReleaseBackingCapacity()
        {
            try
            {
                _testHooks?.ReleaseClaimed?.Invoke();
                _server.ReleaseCall(_connection);
            }
            finally
            {
                // Normal completion publishes Disposed only after both backing
                // capacity scopes have been released. The finally prevents an
                // invariant exception from stranding aliases forever in Releasing.
                Volatile.Write(ref _state, Disposed);
            }
        }
    }
}

internal sealed class ServerRequestPermitTestHooks
{
    internal Action? ReleaseClaimed { get; init; }

    internal Action? DisposeObservedReleasing { get; init; }
}
