using System.Reflection;

namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private readonly Guid _nStudyRegistryEpoch = Guid.NewGuid();
    private long _nStudyPublicationGeneration = 1;

    private static SharpLinkException NStudyBindingRetiredError() =>
        new(SharpLinkErrorCode.Unavailable, "This private binding was retired before business admission.");

    private async ValueTask HandleNStudyBindAsync(
        ServerConnectionState connection, ulong operation, NStudyBind bind, CancellationToken ct)
    {
        var session = connection.Session;
        session.EnsureNStudyReady(RpcSessionRole.Server);
        var table = connection.NStudyBindings;
        table.ObserveOperation(operation);
        var authority = new NStudyAuthority(_desiredSessionServerInstanceId,
            _nStudyRegistryEpoch, connection.NStudyPhysicalIdentity);
        NStudyAck ack = new(NStudyBindStatus.Unavailable, bind.Contract, bind.Hash, authority, 0, default);
        lock (_registryGate)
        {
            if (CurrentState == ServerState.Running && connection.LifecycleState == ServerConnectionLifecycleState.Ready)
            {
                if (!Volatile.Read(ref _services).TryGetValue(bind.Contract, out var service))
                    ack = ack with { Status = NStudyBindStatus.Missing };
                else if (service.NStudyContractHash != bind.Hash)
                    ack = ack with { Status = NStudyBindStatus.Incompatible };
                else if (service.AcceptsCalls)
                {
                    var target = service.NStudyTarget;
                    if (bind.Expected != default && bind.Expected != target)
                        ack = ack with { Status = NStudyBindStatus.Superseded };
                    else if (!session.MayBindNStudyTarget(target))
                        ack = ack with { Status = NStudyBindStatus.Unavailable };
                    else if (table.TryInstall(service, out var token))
                        ack = ack with { Status = NStudyBindStatus.Bound, Target = target, BindingId = token };
                    else
                        ack = ack with { Status = NStudyBindStatus.Full };
                }
            }
        }

        // The successful exact selection is captured before publication.
        // No external work or send-pump wait is part of the registry gate.
        using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(5), _runtimeContext.TimeProvider);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        await session.SendNStudyBindAckAsync(operation, ack, budget.Token).ConfigureAwait(false);
    }

    // Fixture receipt getter queries the actual currently installed Assembly,
    // not a published future/replace receipt API.
    internal NStudyTarget ReadNStudyTarget(Assembly assembly, long contract)
    {
        lock (_registryGate)
        {
            if (!Volatile.Read(ref _services).TryGetValue(contract, out var service) ||
                service.Module is not { } module || !module.SuppliesAssembly(assembly) || !service.AcceptsCalls)
                throw new InvalidOperationException("The specified Assembly is not this contract's current implementation.");
            return service.NStudyTarget;
        }
    }

    // Under the registration gate: no new Bind can select the retiring group.
    // Separate scalar notices from physical module cleanup and send outside it.
    private List<ServerConnectionState>? SealNStudyBindings(SharpLinkDynamicModule module)
    {
        var target = new NStudyTarget(module.NStudyRegistration, module.NStudyPublication);
        List<ServerConnectionState>? workers = null;
        foreach (var connection in _connectionRegistry.SnapshotActive())
        {
            if (connection.NStudyBindings.Seal(target))
                (workers ??= []).Add(connection);
        }
        return workers;
    }

    private void PublishNStudyRetirement(List<ServerConnectionState>? workers)
    {
        if (workers is null)
            return;
        foreach (var connection in workers)
            TrackFrameworkTask(PublishNStudyRetirementAsync(connection), "NStudyBindingInvalidation");
    }

    private async Task PublishNStudyRetirementAsync(ServerConnectionState connection)
    {
        var tokens = new long[NStudyWire.BindingLimit];
        try
        {
            while (true)
            {
                var count = connection.NStudyBindings.TakeInvalidated(tokens);
                if (count == 0)
                    return;
                using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(5), _runtimeContext.TimeProvider);
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionToken, timer.Token);
                budget.Token.ThrowIfCancellationRequested();
                await connection.Session.SendNStudyInvalidatedAsync(tokens.AsMemory(0, count), budget.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (connection.ConnectionToken.IsCancellationRequested)
        {
            // Termination already owns this peer.
        }
        catch (Exception error)
        {
            // A notice with uncertain emission is scoped to this physical peer.
            connection.Session.NotifyDisconnected(error);
            throw;
        }
    }
}

// Release and control use the actual receive loop. The dispatch continuation can
// return its exact physical read on another thread. The lock guards only these
// bounded scalar changes; existing user work is always outside it.
internal sealed class NStudyServerBindings
{
    private readonly Lock _gate = new();
    private Slot[]? _slots;
    private ulong _operation;
    private bool _publishingInvalidations;
    private bool _closed;

    private struct Slot
    {
        internal SharpLinkServer.QueuedServiceSelection Service;
        internal NStudyTarget Target;
        internal uint Incarnation;
        internal uint PendingInvalidation;
        internal int Users;
        internal bool Occupied;
        internal bool Open;
    }

    internal void ObserveOperation(ulong operation)
    {
        if (operation <= _operation)
            throw RpcSession.InvalidNStudy("Private Bind operation did not advance.");
        _operation = operation;
    }

    internal bool TryInstall(ServiceRegistration service, out long bindingId)
    {
        lock (_gate)
        {
            if (_closed)
            {
                bindingId = 0;
                return false;
            }
            var slots = _slots ??= new Slot[NStudyWire.BindingLimit];
            for (var index = 0; index < slots.Length; index++)
            {
                ref var entry = ref slots[index];
                if (entry.Occupied || entry.Incarnation == uint.MaxValue)
                    continue;
                entry.Service = new(service);
                entry.Target = service.NStudyTarget;
                entry.Open = entry.Occupied = true;
                entry.Incarnation++;
                bindingId = NStudyWire.BindingId(index, entry.Incarnation);
                return true;
            }
        }
        bindingId = 0;
        return false;
    }

    internal bool TryResolve(long bindingId, out ServiceRegistration service)
    {
        lock (_gate)
        {
            if (TryFind(bindingId, out var index) && _slots![index].Open)
                return _slots[index].Service.TryGetService(out service);
        }
        service = null!;
        return false;
    }

    internal bool TryBorrow(long bindingId, out ServiceRegistration service, out Use use)
    {
        lock (_gate)
        {
            if (TryFind(bindingId, out var index))
            {
                ref var entry = ref _slots![index];
                if (entry.Open && entry.Service.TryGetService(out service))
                {
                    checked { entry.Users++; }
                    use = new(this, bindingId);
                    return true;
                }
            }
        }
        service = null!;
        use = default;
        return false;
    }

    internal void Release(long bindingId)
    {
        lock (_gate)
        {
            if (!TryFind(bindingId, out var index))
                return;
            ref var entry = ref _slots![index];
            entry.Open = false;
            ReclaimIfIdle(ref entry);
        }
    }

    internal void Close()
    {
        lock (_gate)
        {
            _closed = true;
            if (_slots is not { } slots)
                return;
            foreach (ref var entry in slots.AsSpan())
            {
                entry.Open = false;
                // The closed peer cannot consume new invalidations. Physical borrowers
                // still return their exact original slot through Return.
                entry.PendingInvalidation = 0;
                ReclaimIfIdle(ref entry);
            }
        }
    }

    // The current worker, if any, also owns future notices. Keep at most the
    // newest retired incarnation of each slot, even when that slot is reused.
    internal bool Seal(NStudyTarget target)
    {
        lock (_gate)
        {
            if (_slots is not { } slots)
                return false;
            var marked = false;
            foreach (ref var entry in slots.AsSpan())
            {
                if (!entry.Open || entry.Target != target)
                    continue;
                entry.Open = false;
                entry.PendingInvalidation = entry.Incarnation;
                ReclaimIfIdle(ref entry);
                marked = true;
            }
            if (!marked || _publishingInvalidations)
                return false;
            return _publishingInvalidations = true;
        }
    }

    internal int TakeInvalidated(Span<long> tokens)
    {
        lock (_gate)
        {
            var count = 0;
            var slots = _slots!;
            for (var i = 0; i < slots.Length; i++)
            {
                ref var entry = ref slots[i];
                if (entry.PendingInvalidation == 0)
                    continue;
                tokens[count++] = NStudyWire.BindingId(i, entry.PendingInvalidation);
                entry.PendingInvalidation = 0;
            }
            if (count == 0)
                _publishingInvalidations = false;
            return count;
        }
    }

    internal bool IsRetired(long token)
    {
        lock (_gate)
        {
            var index = NStudyWire.SlotIndex(token);
            var incarnation = NStudyWire.Incarnation(token);
            return incarnation != 0 && _slots is { } slots && (uint)index < (uint)slots.Length &&
                (incarnation < slots[index].Incarnation ||
                 incarnation == slots[index].Incarnation && !slots[index].Open);
        }
    }

    private bool ClaimStart(long bindingId)
    {
        lock (_gate)
            return TryFind(bindingId, out var index) && _slots![index].Open;
    }

    private void Return(long bindingId)
    {
        lock (_gate)
        {
            if (!TryFind(bindingId, out var index) || _slots![index].Users <= 0)
                throw new InvalidOperationException("The exact N-study binding use was returned without an owner.");
            ref var entry = ref _slots[index];
            entry.Users--;
            ReclaimIfIdle(ref entry);
        }
    }

    private bool TryFind(long bindingId, out int index)
    {
        index = NStudyWire.SlotIndex(bindingId);
        return _slots is { } slots && (uint)index < (uint)slots.Length &&
               slots[index].Occupied && slots[index].Incarnation == NStudyWire.Incarnation(bindingId);
    }

    private static void ReclaimIfIdle(ref Slot entry)
    {
        if (!entry.Open && entry.Users == 0)
        {
            entry.Service = default;
            entry.Target = default;
            entry.Occupied = false;
        }
    }

    internal (int Open, int Retiring, int Users) Snapshot()
    {
        lock (_gate)
        {
            var open = 0;
            var retiring = 0;
            var users = 0;
            if (_slots is not null)
            {
                foreach (var slot in _slots)
                {
                    if (slot.Open) open++;
                    else if (slot.Occupied) retiring++;
                    users += slot.Users;
                }
            }
            return (open, retiring, users);
        }
    }

    internal bool HasOpenTarget(NStudyTarget target)
    {
        lock (_gate)
        {
            if (_slots is { } slots)
            {
                foreach (var slot in slots)
                    if (slot.Open && slot.Target == target)
                        return true;
            }
            return false;
        }
    }

    // Only DispatchRequestAsync owns Return. Queued continuations borrow this
    // scalar identity to arbitrate their eventual start, never return the use.
    internal readonly struct Use(NStudyServerBindings owner, long bindingId) : IDisposable
    {
        internal bool ClaimStart() => owner?.ClaimStart(bindingId) == true;
        public void Dispose() => owner?.Return(bindingId);
    }
}
