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
        private readonly Lock _resourceGate = new();
        private ServerDecodePermit? _decodePermit;
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

        /// <summary>
        /// Reserves the server-wide decode concurrency credit and any compressed bytes that must
        /// outlive the current reader-loop frame. The resulting permit is attached to this request
        /// owner so cancellation/disposal cannot orphan decode resources.
        /// </summary>
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

                if (!_server.ResourceGovernor.TryAcquireDecode(retainedCompressedBytes, out decodePermit))
                    return false;

                _decodePermit = decodePermit;
                return true;
            }
        }

        /// <summary>
        /// Acquires decode concurrency by transferring an already-accounted retained compressed
        /// owner into this request. This is used when admission or the decode executor must keep the
        /// compressed frame alive before provider execution begins.
        /// </summary>
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

                if (!_server.ResourceGovernor.TryAcquireDecode(retainedPermit, out decodePermit))
                    return false;

                _decodePermit = decodePermit;
                return true;
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

                // Capacity was deliberately acquired during TryReserveCall. There is
                // no counter transfer here yet: this slice introduces the unique owner
                // while preserving the existing Stop/Drain linearization unchanged.
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
                    decodePermit = _decodePermit;

                try
                {
                    decodePermit?.Dispose();
                }
                finally
                {
                    _server.ReleaseCall(_connection);
                }
            }
            finally
            {
                // Normal completion publishes Disposed only after request-owned decode
                // resources and both backing call-capacity scopes have been released.
                // The finally prevents an invariant exception from stranding aliases forever.
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
