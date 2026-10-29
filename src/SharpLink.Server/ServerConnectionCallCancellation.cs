namespace SharpLink.Server;

/// <summary>
/// Two infrastructure registrations per connection cover exactly matching call sources.
/// Unmatched sources keep per-call registrations. Publication uses the existing map stripe
/// ordering plus a token recheck; only cancellation scans the call table. Invocation CTSs and
/// module registrations remain private to each call.
/// </summary>
internal sealed class ServerConnectionCallCancellation : IDisposable
{
    private readonly StripedLongMap<ServerCallCancellationState> _calls;
    private readonly CancellationToken _serverStopping;
    private readonly CancellationToken _connectionClosed;
    private readonly Func<List<ServerCallCancellationLease>> _capture;
    private readonly Lock _registrationGate = new();
    private bool _disposed;
    private CancellationTokenRegistration _serverRegistration;
    private CancellationTokenRegistration _connectionRegistration;

    internal ServerConnectionCallCancellation(
        StripedLongMap<ServerCallCancellationState> calls,
        CancellationToken serverStopping,
        CancellationToken connectionClosed,
        Func<CancellationToken, Action<object?>, object, CancellationTokenRegistration>? register = null,
        Func<List<ServerCallCancellationLease>>? capture = null)
    {
        _calls = calls ?? throw new ArgumentNullException(nameof(calls));
        _serverStopping = serverStopping;
        _connectionClosed = connectionClosed;
        _capture = capture ?? (() => _calls.CaptureEntries(
            static (id, state) => state.CaptureLease(id)));
        register ??= static (token, callback, state) => token.UnsafeRegister(callback, state);
        try
        {
            // All callback-visible fields are initialized. Install after the connection's
            // linked CTS, and before publishing the connection to any request dispatcher.
            if (serverStopping.CanBeCanceled)
                InstallRegistration(register(serverStopping,
                    static state => ((ServerConnectionCallCancellation)state!).CancelCalls(
                        ServerCallCancellationReason.ServerStopping), this), server: true);
            if (connectionClosed.CanBeCanceled)
                InstallRegistration(register(connectionClosed,
                    static state => ((ServerConnectionCallCancellation)state!).CancelCalls(
                        ServerCallCancellationReason.ConnectionClosed), this), server: false);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal bool CoversServerStopping(CancellationToken token)
        => token.CanBeCanceled && token == _serverStopping;

    internal bool CoversConnectionClosed(CancellationToken token)
        => token.CanBeCanceled && token == _connectionClosed;

    /// <summary>
    /// The factory owns this newly rented generation until return; no invocation/queue has yet
    /// received its token. Existing states must not be republished. Late noncooperative tracking
    /// has no invocation CTS even when the user task has already started.
    /// </summary>
    internal void Publish(ServerCallCancellationState call, ServerCallDeadlineScheduler deadlines)
    {
        var lease = call.CaptureLease(call.RequestId);
        var shareServer = call.UsesSharedInfrastructureSource(this, ServerCallCancellationReason.ServerStopping);
        var shareConnection = call.UsesSharedInfrastructureSource(this, ServerCallCancellationReason.ConnectionClosed);
        var acquired = false;
        var ownsGeneration = true;
        Exception? publicationFailure = null;
        try
        {
            _calls.Set(lease.RequestId, call);
            deadlines.Register(call);
            var serverStopped = shareServer && _serverStopping.IsCancellationRequested;
            var connectionClosed = shareConnection && _connectionClosed.IsCancellationRequested;
            if (!serverStopped && !connectionClosed)
                return;

            // Cold path only. Keep the generation through both notifications and any rollback,
            // even if synchronous notification reenters cleanup.
            acquired = lease.TryAcquire();
            if (!acquired)
            {
                ownsGeneration = false;
                throw new InvalidOperationException("The call generation retired during publication.");
            }
            if (serverStopped)
                call.TryCancel(ServerCallCancellationReason.ServerStopping);
            if (connectionClosed)
                call.TryCancel(ServerCallCancellationReason.ConnectionClosed);
        }
        catch (Exception failure)
        {
            publicationFailure = failure;
            if (ownsGeneration)
            {
                try
                {
                    _calls.TryRemove(lease.RequestId, call);
                    call.Dispose();
                }
                catch (Exception cleanupFailure)
                {
                    publicationFailure = new AggregateException(failure, cleanupFailure);
                    throw publicationFailure;
                }
            }
            throw;
        }
        finally
        {
            if (acquired)
            {
                try { lease.ReleaseUse(); }
                catch (Exception releaseFailure) when (publicationFailure is not null)
                {
                    throw new AggregateException(publicationFailure, releaseFailure);
                }
            }
        }
    }

    private void CancelCalls(ServerCallCancellationReason reason)
    {
        // Complete capture before notifying any application. No partial snapshots, retry cap,
        // map draining, or pre-acquired whole-table ownership. Allocation failure propagates to
        // the existing cancellation/close error channel; OOM cannot promise full notification.
        var snapshot = _capture();
        List<Exception>? failures = null;
        foreach (var lease in snapshot)
        {
            if (!lease.TryAcquire())
                continue;
            try
            {
                if (lease.State.UsesSharedInfrastructureSource(this, reason))
                    lease.State.TryCancel(reason);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
            finally
            {
                try { lease.ReleaseUse(); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }
        }
        if (failures is not null)
            throw new AggregateException(failures);
    }

    private void InstallRegistration(CancellationTokenRegistration registration, bool server)
    {
        lock (_registrationGate)
        {
            if (!_disposed)
            {
                if (server) _serverRegistration = registration;
                else _connectionRegistration = registration;
                return;
            }
        }
        registration.Unregister();
    }

    public void Dispose()
    {
        // Never wait on a callback which may itself reenter CloseAsync. Callbacks retain this
        // nonpooled object and use only cached tokens/map/leases, never the session, scheduler,
        // or a disposed CTS.Token getter. A false result is not a reason to wait.
        CancellationTokenRegistration server, connection;
        lock (_registrationGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            server = _serverRegistration;
            connection = _connectionRegistration;
            _serverRegistration = default;
            _connectionRegistration = default;
        }
        server.Unregister();
        connection.Unregister();
    }
}
