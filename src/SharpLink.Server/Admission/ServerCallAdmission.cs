namespace SharpLink.Server;

internal enum ServerCallAdmissionResult : byte
{
    Acquired,
    Unavailable,
    PerConnectionCapacityExhausted,
    ServerCapacityExhausted
}

/// <summary>
/// Owns server-wide call-admission accounting and the local-to-global capacity transfer.
/// Server lifecycle and drain publication remain owned by <see cref="SharpLinkServer"/> and are
/// observed through direct calls on that sealed owner so extraction adds no delegate dispatch to
/// the request hot path.
/// </summary>
internal sealed class ServerCallAdmission
{
    private readonly SharpLinkServer _server;
    private readonly int _maxConcurrentCallsPerConnection;
    private readonly int _maxConcurrentCallsPerServer;
    private int _globalActiveCalls;
    private int _pendingCallAdmissions;

    internal ServerCallAdmission(
        SharpLinkServer server,
        int maxConcurrentCallsPerConnection,
        int maxConcurrentCallsPerServer)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentCallsPerConnection, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentCallsPerServer, 1);
        _server = server;
        _maxConcurrentCallsPerConnection = maxConcurrentCallsPerConnection;
        _maxConcurrentCallsPerServer = maxConcurrentCallsPerServer;
    }

    internal int ActiveCallCount => Volatile.Read(ref _globalActiveCalls);

    internal int PendingCallAdmissions => Volatile.Read(ref _pendingCallAdmissions);

    internal int MaxConcurrentCallsPerConnection => _maxConcurrentCallsPerConnection;

    internal int MaxConcurrentCallsPerServer => _maxConcurrentCallsPerServer;

    internal ServerResourceGovernor ResourceGovernor => _server.ResourceGovernorForCallAdmission;

    internal ServerCallAdmissionResult TryAcquireCall(ServerConnectionState connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!_server.IsRunningForCallAdmission)
            return ServerCallAdmissionResult.Unavailable;

        Interlocked.Increment(ref _pendingCallAdmissions);
        try
        {
            // Stop can begin between the first Running check and the pending increment. Once this
            // check succeeds, the pending count covers every local -> global transfer and rollback.
            if (!_server.IsRunningForCallAdmission)
                return ServerCallAdmissionResult.Unavailable;

            if (!connection.TryAcquireCall(_maxConcurrentCallsPerConnection))
            {
                return connection.LifecycleState == ServerConnectionLifecycleState.Ready
                    ? ServerCallAdmissionResult.PerConnectionCapacityExhausted
                    : ServerCallAdmissionResult.Unavailable;
            }

#if DEBUG
            connection.NotifyAfterLocalCallAdmissionForTesting();
#endif

            if (!TryAcquireGlobalCall())
            {
                // The provisional global increment remains owned until the paired local slot is
                // released so drain cannot observe zero global calls while local ownership remains.
                ReleaseCall(connection);
                return ServerCallAdmissionResult.ServerCapacityExhausted;
            }

            if (_server.IsRunningForCallAdmission)
                return ServerCallAdmissionResult.Acquired;

            ReleaseCall(connection);
            return ServerCallAdmissionResult.Unavailable;
        }
        finally
        {
            EndPendingCallAdmission(connection);
        }
    }

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
            // Capacity is already owned when permit materialization begins. Roll both scopes back
            // synchronously if construction fails so no admitted slot can become orphaned.
            ReleaseCall(connection);
            throw;
        }
    }

    internal void ReleaseCall(ServerConnectionState connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection.ReleaseCall();
        ReleaseGlobalCall();
        if (!_server.IsRunningForCallAdmission)
            _server.TrySignalCallsDrainedForCallAdmission(connection);
    }

    private bool TryAcquireGlobalCall()
    {
        if (Interlocked.Increment(ref _globalActiveCalls) <= _maxConcurrentCallsPerServer)
            return true;

        // The caller owns both provisional slots at this point. It must release the connection slot
        // before decrementing this global slot so server drain cannot become visible between them.
        return false;
    }

    private void ReleaseGlobalCall()
    {
        var active = Interlocked.Decrement(ref _globalActiveCalls);
        if (active < 0)
            throw new InvalidOperationException("Server global active call count underflowed.");
    }

    private void EndPendingCallAdmission(ServerConnectionState connection)
    {
        var remaining = Interlocked.Decrement(ref _pendingCallAdmissions);
        if (remaining < 0)
            throw new InvalidOperationException("Server pending call admission count underflowed.");
        if (remaining == 0 && !_server.IsRunningForCallAdmission)
            _server.TrySignalCallsDrainedForCallAdmission(connection);
    }
}

/// <summary>
/// Unique owner for one accepted request's call capacity and optional decode resources.
/// A Reserved permit already owns local/global call capacity; activation is only a lifecycle phase
/// transition, preserving the existing drain-safe accounting until the two-phase lifecycle changes.
/// </summary>
internal sealed class ServerRequestPermit : IDisposable
{
    private const int Reserved = 0;
    private const int Activating = 1;
    private const int Active = 2;
    private const int Releasing = 3;
    private const int Disposed = 4;

    private readonly ServerCallAdmission _admission;
    private readonly ServerConnectionState _connection;
    private readonly ServerRequestPermitTestHooks? _testHooks;
    private readonly Lock _resourceGate = new();
    private ServerDecodePermit? _decodePermit;
    private int _state = Reserved;

    internal ServerRequestPermit(
        ServerCallAdmission admission,
        ServerConnectionState connection,
        ServerRequestPermitTestHooks? testHooks)
    {
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _testHooks = testHooks;
    }

    internal bool IsReserved => Volatile.Read(ref _state) == Reserved;

    internal bool IsActive => Volatile.Read(ref _state) == Active;

    internal bool TryAcquireDecodePermit(
        long retainedCompressedBytes,
        out ServerDecodePermit? decodePermit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retainedCompressedBytes);

        lock (_resourceGate)
        {
            if (Volatile.Read(ref _state) != Reserved || _decodePermit is not null)
            {
                decodePermit = null;
                return false;
            }

            if (!_admission.ResourceGovernor.TryAcquireDecode(retainedCompressedBytes, out decodePermit))
                return false;

            _decodePermit = decodePermit;
            return true;
        }
    }

    internal bool TryAcquireDecodePermit(
        ServerRetainedCompressedPermit retainedPermit,
        out ServerDecodePermit? decodePermit)
    {
        ArgumentNullException.ThrowIfNull(retainedPermit);

        lock (_resourceGate)
        {
            if (Volatile.Read(ref _state) != Reserved || _decodePermit is not null)
            {
                decodePermit = null;
                return false;
            }

            if (!_admission.ResourceGovernor.TryAcquireDecode(retainedPermit, out decodePermit))
                return false;

            _decodePermit = decodePermit;
            return true;
        }
    }

    internal void ReleaseDecodeResources()
    {
        ServerDecodePermit? decodePermit;
        lock (_resourceGate)
        {
            var current = Volatile.Read(ref _state);
            if (current is Activating or Active)
            {
                throw new InvalidOperationException(
                    "Decode resources cannot be detached after call activation.");
            }
            if (current is Releasing or Disposed)
                return;

            decodePermit = _decodePermit;
            _decodePermit = null;
        }

        decodePermit?.Dispose();
    }

    internal void TransferDecodedBytesTo(ServerCallCancellationState callState)
    {
        ArgumentNullException.ThrowIfNull(callState);

        ServerDecodedBytesPermit? decodedBytesPermit;
        lock (_resourceGate)
        {
            var decodePermit = _decodePermit;
            if (decodePermit is null)
                return;
            decodedBytesPermit = decodePermit.DetachDecodedBytesOwnership();
        }

        if (decodedBytesPermit is null)
            return;

        try
        {
            callState.AttachDecodedBytesPermit(decodedBytesPermit);
        }
        catch
        {
            decodedBytesPermit.Dispose();
            throw;
        }
    }

    internal void Activate()
    {
        lock (_resourceGate)
        {
            var current = Volatile.Read(ref _state);
            if (current is Releasing or Disposed)
                throw new ObjectDisposedException(nameof(ServerRequestPermit));
            if (current != Reserved)
                throw new InvalidOperationException("Only a reserved call permit can be activated.");
            if (_decodePermit is not null && !_decodePermit.IsDecodeCompleted)
            {
                throw new InvalidOperationException(
                    "A request with decode resources cannot be activated before decode completes.");
            }

            var observed = Interlocked.CompareExchange(ref _state, Activating, Reserved);
            if (observed != Reserved)
            {
                if (observed is Releasing or Disposed)
                    throw new ObjectDisposedException(nameof(ServerRequestPermit));
                throw new InvalidOperationException("Only a reserved call permit can be activated.");
            }

            Volatile.Write(ref _state, Active);
        }
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
                    if (!TryClaimRelease(Reserved))
                        continue;
                    ReleaseBackingCapacity();
                    return;
                case Activating:
                    spinner.SpinOnce();
                    continue;
                case Active:
                    if (!TryClaimRelease(Active))
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

    private bool TryClaimRelease(int expectedState)
    {
        lock (_resourceGate)
        {
            if (Volatile.Read(ref _state) != expectedState)
                return false;
            return Interlocked.CompareExchange(ref _state, Releasing, expectedState) == expectedState;
        }
    }

    private void ReleaseBackingCapacity()
    {
        try
        {
            _testHooks?.ReleaseClaimed?.Invoke();
            ServerDecodePermit? decodePermit;
            lock (_resourceGate)
            {
                decodePermit = _decodePermit;
                _decodePermit = null;
            }

            try
            {
                decodePermit?.Dispose();
            }
            finally
            {
                _admission.ReleaseCall(_connection);
            }
        }
        finally
        {
            Volatile.Write(ref _state, Disposed);
        }
    }
}

internal sealed class ServerRequestPermitTestHooks
{
    internal Action? ReleaseClaimed { get; init; }

    internal Action? DisposeObservedReleasing { get; init; }
}
