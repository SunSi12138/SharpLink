using System.Reflection;

namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private readonly Guid _nStudyRegistryEpoch = Guid.NewGuid();
    private long _nStudyPublicationGeneration = 1;

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

// One physical server receive-loop owner. The bounded checkpoint never recycles
// a slot; the selected identity cannot keep a retired dynamic implementation alive.
internal sealed class NStudyServerBindings
{
    private SharpLinkServer.QueuedServiceSelection[]? _entries;
    private int _used;
    private ulong _operation;

    internal void ObserveOperation(ulong operation)
    {
        if (operation <= _operation)
            throw RpcSession.InvalidNStudy("Private Bind operation did not advance.");
        _operation = operation;
    }

    internal bool TryInstall(ServiceRegistration service, out long bindingId)
    {
        if (_used >= NStudyWire.BindingLimit)
        {
            bindingId = 0;
            return false;
        }
        (_entries ??= new SharpLinkServer.QueuedServiceSelection[NStudyWire.BindingLimit])[_used] = new(service);
        bindingId = (1L << 32) | (uint)++_used;
        return true;
    }

    internal bool TryResolve(long bindingId, out ServiceRegistration service)
    {
        var slot = (uint)bindingId;
        if ((bindingId >> 32) == 1 && slot > 0 && slot <= (uint)_used)
            return _entries![(int)slot - 1].TryGetService(out service);
        service = null!;
        return false;
    }
}
