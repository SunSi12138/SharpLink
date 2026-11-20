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
}

// Release and control use the actual receive loop. The dispatch continuation can
// return its exact physical read on another thread. The lock guards only these
// bounded scalar changes; existing user work is always outside it.
internal sealed class NStudyServerBindings
{
    private readonly Lock _gate = new();
    private Slot[]? _slots;
    private ulong _operation;

    private struct Slot
    {
        internal SharpLinkServer.QueuedServiceSelection Service;
        internal uint Incarnation;
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
            var slots = _slots ??= new Slot[NStudyWire.BindingLimit];
            for (var index = 0; index < slots.Length; index++)
            {
                ref var entry = ref slots[index];
                if (entry.Occupied || entry.Incarnation == uint.MaxValue)
                    continue;
                entry.Service = new(service);
                entry.Open = entry.Occupied = true;
                entry.Incarnation++;
                bindingId = unchecked((long)(((ulong)entry.Incarnation << 32) | (uint)(index + 1)));
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
        index = unchecked((int)(uint)bindingId - 1);
        return _slots is { } slots && (uint)index < (uint)slots.Length &&
               slots[index].Occupied && slots[index].Incarnation == (uint)((ulong)bindingId >> 32);
    }

    private static void ReclaimIfIdle(ref Slot entry)
    {
        if (!entry.Open && entry.Users == 0)
        {
            entry.Service = default;
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

    // Only DispatchRequestAsync owns Return. Queued continuations borrow this
    // scalar identity to arbitrate their eventual start, never return the use.
    internal readonly struct Use(NStudyServerBindings owner, long bindingId) : IDisposable
    {
        internal bool ClaimStart() => owner?.ClaimStart(bindingId) == true;
        public void Dispose() => owner?.Return(bindingId);
    }
}
