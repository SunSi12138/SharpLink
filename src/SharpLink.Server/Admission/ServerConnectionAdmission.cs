using System.Diagnostics;

namespace SharpLink.Server;

/// <summary>Stable rejection reasons recorded by connection admission telemetry and logs.</summary>
internal static class ConnectionAdmissionRejectionReason
{
    internal const string ConnectionLimit = "connection_limit";
    internal const string HandshakeLimit = "handshake_limit";
}

/// <summary>
/// Owns the two pre-call connection resource bounds of one server: the live accepted
/// connection set and the concurrently handshaking subset. Acquisition is a single
/// interlocked increment; every lease releases exactly once, so the counters are the
/// single source of truth for admission, diagnostics, and tests. Runtime limit changes
/// publish one immutable target pair and never replace this accounting domain.
/// </summary>
internal sealed class ServerConnectionAdmission
{
    private AdmissionTargets _targets;
    private int _activeConnections;
    private int _activeHandshakes;

    internal ServerConnectionAdmission(int maxConnections, int maxHandshakes)
        => _targets = AdmissionTargets.Create(maxConnections, maxHandshakes);

    internal int MaxConnections => Volatile.Read(ref _targets).MaxConnections;

    internal int MaxHandshakes => Volatile.Read(ref _targets).MaxHandshakes;

    internal int ActiveConnections => Volatile.Read(ref _activeConnections);

    internal int ActiveHandshakes => Volatile.Read(ref _activeHandshakes);

    internal void UpdateTargets(int maxConnections, int maxHandshakes)
    {
        var candidate = AdmissionTargets.Create(maxConnections, maxHandshakes);
        Volatile.Write(ref _targets, candidate);
    }

    internal bool TryAcquireConnection(out Lease lease)
    {
        var active = Interlocked.Increment(ref _activeConnections);
        var target = Volatile.Read(ref _targets).MaxConnections;
        if (active > target)
        {
            Interlocked.Decrement(ref _activeConnections);
            lease = null!;
            return false;
        }

        lease = new Lease(this);
        SharpLinkTelemetry.AddAdmittedConnections(1);
        return true;
    }

    internal bool TryAcquireHandshake(Lease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var active = Interlocked.Increment(ref _activeHandshakes);
        var target = Volatile.Read(ref _targets).MaxHandshakes;
        if (active > target)
        {
            Interlocked.Decrement(ref _activeHandshakes);
            return false;
        }

        lease.MarkHandshakeHeld();
        SharpLinkTelemetry.AddActiveHandshakes(1);
        return true;
    }

    private void ReleaseConnection()
    {
        var remaining = Interlocked.Decrement(ref _activeConnections);
        Debug.Assert(remaining >= 0, "Server connection admission counter underflowed.");
        SharpLinkTelemetry.AddAdmittedConnections(-1);
    }

    private void ReleaseHandshake()
    {
        var remaining = Interlocked.Decrement(ref _activeHandshakes);
        Debug.Assert(remaining >= 0, "Server handshake admission counter underflowed.");
        SharpLinkTelemetry.AddActiveHandshakes(-1);
    }

    private sealed class AdmissionTargets
    {
        private AdmissionTargets(int maxConnections, int maxHandshakes)
        {
            MaxConnections = maxConnections;
            MaxHandshakes = maxHandshakes == 0 ? maxConnections : maxHandshakes;
        }

        internal int MaxConnections { get; }

        internal int MaxHandshakes { get; }

        internal static AdmissionTargets Create(int maxConnections, int maxHandshakes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConnections);
            ArgumentOutOfRangeException.ThrowIfNegative(maxHandshakes);
            if (maxHandshakes > maxConnections)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxHandshakes),
                    "MaxConcurrentHandshakes must not exceed MaxConcurrentConnections.");
            }

            return new AdmissionTargets(maxConnections, maxHandshakes);
        }
    }

    /// <summary>
    /// One lease per admitted connection. It carries both the connection slot (acquired in
    /// the accept loop) and, once <see cref="TryAcquireHandshake"/> succeeds, the handshake
    /// slot. Both releases are idempotent, so the terminal cleanup path can release them
    /// unconditionally while the Ready transition releases the handshake slot early.
    /// </summary>
    internal sealed class Lease
    {
        private readonly ServerConnectionAdmission _owner;
        private int _connectionReleased;
        private int _handshakeHeld;
        private int _handshakeReleased;

        internal Lease(ServerConnectionAdmission owner)
            => _owner = owner;

        internal void MarkHandshakeHeld()
            => Volatile.Write(ref _handshakeHeld, 1);

        internal void ReleaseConnection()
        {
            if (Interlocked.Exchange(ref _connectionReleased, 1) == 0)
                _owner.ReleaseConnection();
        }

        internal void ReleaseHandshake()
        {
            if (Volatile.Read(ref _handshakeHeld) == 0 ||
                Interlocked.Exchange(ref _handshakeReleased, 1) != 0)
            {
                return;
            }
            _owner.ReleaseHandshake();
        }
    }
}
