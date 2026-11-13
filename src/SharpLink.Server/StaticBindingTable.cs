#if SHARPLINK_775_STATIC_SLICE
namespace SharpLink.Server;

internal sealed record StaticBindingMethod
{
    internal const ProtocolV2FrameFlags ShapeMask = ProtocolV2FrameFlags.OneWay | ProtocolV2FrameFlags.HasReturn;
    internal RpcMethodDescriptor Descriptor { get; }
    internal bool SupportsCancellation => Descriptor.SupportsCancellation;
    internal int RequiredRequestFlags { get; }

    internal StaticBindingMethod(RpcMethodDescriptor descriptor)
    {
        Descriptor = descriptor;
        RequiredRequestFlags = descriptor.Kind is not (RpcMethodKind.Unary or RpcMethodKind.OneWay) ||
            descriptor.HasClientStreams || descriptor.ClientStreamCount != 0 ? -1 :
            (int)((descriptor.Kind == RpcMethodKind.OneWay ? ProtocolV2FrameFlags.OneWay : 0) |
                (descriptor.HasResponsePayload ? ProtocolV2FrameFlags.HasReturn : 0));
    }
}

// Full token = per-slot incarnation:uint32 + one-based slot:uint32. Entries are immutable;
// detaching clears collectible payload before reuse, and no incarnation ever wraps.
internal sealed class StaticBindingTable(StaticBindingBudget ownerBudget)
{
    private readonly Lock _gate = new();
    private readonly Entry?[] _slots = new Entry?[StaticBindingWire.Capacity];
    private readonly uint[] _incarnations = new uint[StaticBindingWire.Capacity];
    private int _count;
    private bool _closed;
    private ulong _lastOperation;

    internal void ObserveOperation(ulong operation)
    {
        lock (_gate)
        {
            if (operation == 0 || operation <= _lastOperation)
                throw StaticBindingWire.Malformed("Bind operation IDs must increase within this Session.");
            _lastOperation = operation;
        }
    }
    internal int Count { get { lock (_gate) return _count; } }

    internal StaticBindingAck Install(long contractId, ServiceRegistration registration,
        SharpLinkGeneratedContractDescriptor contract, long generation)
    {
        lock (_gate)
        {
            if (_closed || !registration.AcceptsCalls) return Failure(StaticBindingFailure.Unavailable, contractId);
            var slot = -1;
            for (var i = 0; i < _slots.Length; i++)
                if (_slots[i] is null && _incarnations[i] != uint.MaxValue) { slot = i; break; }
            if (slot < 0 || contract.Methods.Count > StaticBindingWire.MaxMethods)
                return Failure(StaticBindingFailure.Capacity, contractId);
            var methods = new Dictionary<long, StaticBindingMethod>();
            foreach (var method in contract.Methods)
            {
                if (!registration.Stub.TryResolveMethod(method.MethodId, out var descriptor) ||
                    !ResolvedMethodValidation.IsValid(descriptor, contractId, method.MethodId) ||
                    descriptor != method.Facts || descriptor.Kind != method.Kind ||
                    descriptor.SupportsCancellation != method.SupportsCancellation)
                    return Failure(StaticBindingFailure.UnsupportedShape, contractId);
                if (!methods.TryAdd(method.MethodId, new StaticBindingMethod(descriptor)))
                    return Failure(StaticBindingFailure.UnsupportedShape, contractId);
            }
            var lease = ownerBudget.TryReserveBinding();
            if (lease is null) return Failure(StaticBindingFailure.Capacity, contractId);
            var token = ((ulong)++_incarnations[slot] << 32) | (uint)(slot + 1);
            var ack = new StaticBindingAck(StaticBindingFailure.None, contractId, token,
                registration.StaticBindingRegistrationId, generation);
            Volatile.Write(ref _slots[slot], new Entry(ack, registration.Module,
                new Payload(registration, methods.ToFrozenDictionary()), lease));
            _count++;
            return ack;
        }
    }

    internal StaticBindingFailure Acquire(ulong token, long methodId, ProtocolV2FrameFlags flags,
        out BoundRequestSelection selection)
    {
        selection = default;
        var failure = Find(token, out var entry);
        if (failure != StaticBindingFailure.None) return failure;
        SharpLinkDynamicModuleLease protection = default;
        if (entry!.Module is { } module && !module.TryAcquire(false, out protection))
            return StaticBindingFailure.Retired;
        var transferred = false;
        try
        {
            // No collectible payload, Stub or method metadata is touched before protection.
            if (Volatile.Read(ref entry.Value) is not { } value) return StaticBindingFailure.Retired;
            if (!value.Methods.TryGetValue(methodId, out var method)) return StaticBindingFailure.UnknownMethod;
            if ((int)(flags & StaticBindingMethod.ShapeMask) != method.RequiredRequestFlags)
                return StaticBindingFailure.UnsupportedShape;
            selection = new BoundRequestSelection(value.Registration, method, protection);
            transferred = true;
            return StaticBindingFailure.None;
        }
        finally { if (!transferred) protection.Dispose(); }
    }

    // The pre-existing pure static table controls do not borrow a dynamic lease.
    internal StaticBindingFailure Resolve(ulong token, long methodId, ProtocolV2FrameFlags flags,
        out ServiceRegistration registration, out StaticBindingMethod method)
    {
        registration = null!; method = null!;
        var failure = Find(token, out var entry);
        if (failure != StaticBindingFailure.None) return failure;
        if (entry!.Module is not null) throw new InvalidOperationException("Dynamic dispatch requires Acquire.");
        failure = Acquire(token, methodId, flags, out var selection);
        if (failure == StaticBindingFailure.None) { registration = selection.Registration; method = selection.Method; }
        selection.Dispose();
        return failure;
    }

    private StaticBindingFailure Find(ulong token, out Entry? entry)
    {
        entry = null;
        var slot = unchecked((uint)token);
        if (slot == 0 || slot > _slots.Length || Volatile.Read(ref _closed)) return StaticBindingFailure.UnknownBinding;
        entry = Volatile.Read(ref _slots[(int)slot - 1]);
        if (entry?.Ack.Token == token) return StaticBindingFailure.None;
        return (uint)(token >> 32) != 0 && (uint)(token >> 32) <= Volatile.Read(ref _incarnations[(int)slot - 1])
            ? StaticBindingFailure.Retired : StaticBindingFailure.UnknownBinding;
    }

    internal ulong[] PrepareRetirement(SharpLinkDynamicModule module)
    {
        lock (_gate)
            return _slots.Where(entry => ReferenceEquals(entry?.Module, module)).Select(entry => entry!.Ack.Token).ToArray();
    }

    internal bool Release(ulong token)
    {
        lock (_gate)
        {
            var slot = unchecked((uint)token);
            if (slot == 0 || slot > _slots.Length || _slots[(int)slot - 1]?.Ack.Token != token) return false;
            var entry = Interlocked.Exchange(ref _slots[(int)slot - 1], null)!;
            Interlocked.Exchange(ref entry.Value, null); // stale Entry readers cannot root the old ALC.
            entry.Lease.Dispose();
            _count--;
            return true;
        }
    }

    internal void Close()
    {
        lock (_gate)
        {
            Volatile.Write(ref _closed, true);
            for (var i = 0; i < _slots.Length; i++)
                if (_slots[i] is { } entry) Release(entry.Ack.Token);
        }
    }

    internal static StaticBindingAck Failure(StaticBindingFailure failure, long contractId) => new(failure, contractId, 0, 0, 0);
    private sealed record Payload(ServiceRegistration Registration, FrozenDictionary<long, StaticBindingMethod> Methods);
    private sealed class Entry(StaticBindingAck ack, SharpLinkDynamicModule? module, Payload value, StaticBindingBudget.Lease lease)
    {
        internal StaticBindingAck Ack { get; } = ack;
        internal SharpLinkDynamicModule? Module { get; } = module;
        internal Payload? Value = value;
        internal StaticBindingBudget.Lease Lease { get; } = lease;
    }
}
#endif
