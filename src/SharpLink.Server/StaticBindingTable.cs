#if SHARPLINK_STATIC_BINDING_PROTOTYPE
namespace SharpLink.Server;

internal sealed record StaticBindingMethod(RpcMethodDescriptor Descriptor, bool SupportsCancellation);

// Stage 1 deliberately never recycles a slot before the physical Session is destroyed.
// Layout: incarnation=1 in high 32 bits, one-based slot in low 32 bits. No wrap/reuse.
internal sealed class StaticBindingTable
{
    private readonly Lock _gate = new();
    private readonly Entry?[] _slots = new Entry?[StaticBindingWire.Capacity];
    private int _count;
    private bool _closed;
    internal int Count { get { lock (_gate) return _count; } }

    internal StaticBindingAck Install(long contractId, ServiceRegistration registration,
        SharpLinkGeneratedContractDescriptor contract, long generation)
    {
        lock (_gate)
        {
            if (_closed) return Failure(StaticBindingFailure.Unavailable, contractId);
            if (registration.HasModule) return Failure(StaticBindingFailure.DynamicUnsupported, contractId);
            for (var index = 0; index < _count; index++)
            {
                var existing = _slots[index]!;
                if (ReferenceEquals(existing.Registration, registration)) return existing.Ack;
            }
            if (_count == _slots.Length || contract.Methods.Count > StaticBindingWire.MaxMethods)
                return Failure(StaticBindingFailure.Capacity, contractId);
            var methods = new Dictionary<long, StaticBindingMethod>();
            foreach (var method in contract.Methods)
            {
                if (!registration.Stub.TryGetMethodDescriptor(method.MethodId, out var descriptor) ||
                    descriptor.ContractId != contractId || descriptor.MethodId != method.MethodId ||
                    descriptor.Kind != method.Kind)
                    return Failure(StaticBindingFailure.UnsupportedShape, contractId);
                methods.Add(method.MethodId, new StaticBindingMethod(descriptor, method.SupportsCancellation));
            }
            var token = (1UL << 32) | (uint)(_count + 1);
            var ack = new StaticBindingAck(StaticBindingFailure.None, contractId, token,
                registration.StaticBindingRegistrationId, generation);
            var entry = new Entry(ack, registration, methods.ToFrozenDictionary());
            Volatile.Write(ref _slots[_count], entry);
            _count++;
            return ack;
        }
    }

    internal StaticBindingFailure Resolve(ulong token, long methodId, ProtocolV2FrameFlags flags,
        out ServiceRegistration registration, out StaticBindingMethod method)
    {
        registration = null!;
        method = null!;
        var slot = unchecked((uint)token);
        if (slot == 0 || slot > (uint)_slots.Length)
            return StaticBindingFailure.UnknownBinding;
        var entry = Volatile.Read(ref _slots[(int)slot - 1]);
        if (entry is null || entry.Ack.Token != token)
            return StaticBindingFailure.UnknownBinding;
        if (!entry.Methods.TryGetValue(methodId, out method!))
            return StaticBindingFailure.UnknownMethod;
        var descriptor = method.Descriptor;
        if (descriptor.Kind is not (RpcMethodKind.Unary or RpcMethodKind.OneWay) ||
            descriptor.HasClientStreams || descriptor.ClientStreamCount != 0 ||
            ((flags & ProtocolV2FrameFlags.OneWay) != 0) != (descriptor.Kind == RpcMethodKind.OneWay) ||
            ((flags & ProtocolV2FrameFlags.HasReturn) != 0) != descriptor.HasResponsePayload)
            return StaticBindingFailure.UnsupportedShape;
        registration = entry.Registration;
        return StaticBindingFailure.None;
    }

    internal void Close()
    {
        lock (_gate)
        {
            _closed = true;
            for (var index = 0; index < _slots.Length; index++) Volatile.Write(ref _slots[index], null);
            _count = 0;
        }
    }

    internal static StaticBindingAck Failure(StaticBindingFailure failure, long contractId)
        => new(failure, contractId, 0, 0, 0);

    private sealed record Entry(StaticBindingAck Ack, ServiceRegistration Registration,
        FrozenDictionary<long, StaticBindingMethod> Methods);
}
#endif
