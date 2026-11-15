using System.Collections.Frozen;

namespace SharpLink.Server;

internal sealed class ServerBindingEntry
{
    internal readonly Lock Gate = new();
    internal readonly HashSet<ServerBindingRequest> Readers = [];
    internal ServerBindingEntry(ulong token, ServiceRegistration service, FrozenDictionary<long, RpcMethodDescriptor> methods,
        long registrationId, long generation, ContractBindingQuota quota)
    { Token = token; Service = service; Methods = methods; RegistrationId = registrationId; Generation = generation; _quota = quota; }
    internal ulong Token { get; }
    internal ServiceRegistration? Service { get; private set; }
    internal FrozenDictionary<long, RpcMethodDescriptor>? Methods { get; private set; }
    internal long RegistrationId { get; }
    internal long Generation { get; }
    private readonly ContractBindingQuota _quota;
    internal bool Retired { get; private set; }
    internal void Retire()
    {
        ServerBindingRequest[] waiting;
        lock (Gate)
        {
            if (Retired) return;
            waiting = [.. Readers];
            Retired = true;
            foreach (var request in waiting) request.RejectWaiting();
            TryDetach();
        }
        foreach (var request in waiting) request.NotifyRejected();
    }
    internal void TryDetach()
    {
        if (Retired && Readers.Count == 0 && Service is not null)
        { Service = null; Methods = null; _quota.ReturnBinding(); }
    }
}

/// <summary>Single release authority, moved from the dispatch root into its original physical permit.</summary>
internal sealed class ServerBindingRequest : ServerRequestPermit
{
    private readonly ServerBindingSession _session;
    private readonly ContractBindingQuota _quota;
    private ServerBindingEntry? _entry;
    private ServerCallCancellationLease _waitingLease;
    private bool _claimed, _rejected;
    private int _transferred;
    internal ServerBindingRequest(ServerBindingSession session, ServerBindingEntry entry, long requestId,
        ServiceRegistration service, RpcMethodDescriptor method, ContractBindingQuota quota,
        ServerCallAdmission admission, ServerConnectionState connection, bool mayDecode)
        : base(admission, connection, testHooks: null, mayDecode, capacityOwned: false)
    { _quota = quota; _session = session; _entry = entry; RequestId = requestId; Token = entry.Token; Service = service; Method = method; }
    internal ulong Token { get; }
    internal long RequestId { get; }
    internal ServiceRegistration Service { get; }
    internal RpcMethodDescriptor Method { get; }
    internal bool RejectWaiting()
    { if (_claimed) return false; _rejected = true; return true; }
    internal void NotifyRejected()
    {
        ServerCallCancellationLease lease;
        var entry = Volatile.Read(ref _entry);
        if (entry is null) return;
        lock (entry.Gate) lease = _waitingLease;
        if (!lease.TryAcquire()) return;
        try { lease.State.TryCancel(ServerCallCancellationReason.ModuleDraining); }
        finally { lease.ReleaseUse(); }
    }
    internal void RegisterWaiting(ServerCallCancellationState state)
    {
        var entry = _entry ?? throw new ObjectDisposedException(nameof(ServerBindingRequest));
        lock (entry.Gate) { _waitingLease = state.CaptureLease(RequestId); if (!_rejected && !entry.Retired) return; _rejected = true; }
        state.TryCancel(ServerCallCancellationReason.ModuleDraining);
    }
    internal void ClaimBusinessStart()
    {
        var entry = _entry ?? throw new ObjectDisposedException(nameof(ServerBindingRequest));
        lock (entry.Gate)
        {
            if (_rejected || entry.Retired) throw ContractBindingWire.Error(ContractBindStatus.Retired);
            _claimed = true; _waitingLease = default;
        }
    }
    internal void TransferToPermit() => Volatile.Write(ref _transferred, 1);
    internal void ReleaseUntransferred() { if (Volatile.Read(ref _transferred) == 0) Dispose(); }
    protected override void ReleaseBindingProtection()
    {
        var entry = Interlocked.Exchange(ref _entry, null);
        if (entry is null) return;
        lock (entry.Gate) { _waitingLease = default; entry.Readers.Remove(this); entry.TryDetach(); }
        _session.RemoveRequest(RequestId);
        _quota.ReturnProtectedRequest();
    }
}

internal sealed class ServerBindingSession
{
    private readonly Lock _gate = new();
    private readonly ServerBindingEntry?[] _slots = new ServerBindingEntry?[64];
    private readonly uint[] _incarnations = new uint[64];
    private readonly Dictionary<long, ServerBindingRequest> _requests = [];
    private ulong _lastOperation;
    private bool _closed;
    internal void AcceptOperation(ulong operation)
    {
        lock (_gate)
        {
            if (operation == 0 || operation <= _lastOperation) throw ContractBindingWire.Violation("Bind operation ID is not increasing.");
            _lastOperation = operation;
        }
    }
    internal ContractBindAck Install(ServiceRegistration service, FrozenDictionary<long, RpcMethodDescriptor> methods,
        long contractId, long registrationId, long generation, ContractBindingQuota quota)
    {
        lock (_gate)
        {
            if (_closed) return new(ContractBindStatus.Unavailable, contractId, 0, 0, 0);
            for (var index = 0; index < 64; index++)
            {
                if (_slots[index] is { Service: not null } || _incarnations[index] == uint.MaxValue) continue;
                if (!quota.TryReserveBinding()) break;
                var incarnation = ++_incarnations[index];
                var token = ((ulong)incarnation << 32) | (uint)(index + 1);
                try { _slots[index] = new(token, service, methods, registrationId, generation, quota); }
                catch { quota.ReturnBinding(); throw; }
                return new(ContractBindStatus.Success, contractId, token, registrationId, generation);
            }
            return new(ContractBindStatus.Capacity, contractId, 0, 0, 0);
        }
    }
    internal ServerBindingRequest Capture(long requestId, ReadOnlySequence<byte> payload, ProtocolV2FrameFlags flags, ContractBindingQuota quota, ServerCallAdmission admission, ServerConnectionState connection)
    {
        var reader = new SequenceReader<byte>(payload);
        if (!reader.TryReadLittleEndian(out long tokenBits) || !reader.TryReadLittleEndian(out long methodId))
            throw ContractBindingWire.Violation("Request token prefix is truncated.");
        var token = unchecked((ulong)tokenBits);
        lock (_gate)
        {
            if (!ContractBindingWire.IsToken(token) || _slots[(uint)token - 1] is not { } entry || entry.Token != token)
                throw ContractBindingWire.Error(ContractBindStatus.UnknownBinding);
            lock (entry.Gate)
            {
                if (_closed || entry.Retired || entry.Service is not { } service || entry.Methods is not { } methods)
                    throw ContractBindingWire.Error(ContractBindStatus.Retired);
                if (!methods.TryGetValue(methodId, out var method)) throw ContractBindingWire.Error(ContractBindStatus.UnknownMethod);
                if (method.Kind == RpcMethodKind.OneWay && method.ClientStreamCount != 0)
                    throw ContractBindingWire.Violation("Type4 OneWay request omitted its required input count.");
                if (!ContractMethodFacts.IsStaticSlice(method)) throw ContractBindingWire.Error(ContractBindStatus.UnsupportedShape);
                var oneWay = (flags & ProtocolV2FrameFlags.OneWay) != 0;
                var hasReturn = (flags & ProtocolV2FrameFlags.HasReturn) != 0;
                if (oneWay != (method.Kind == RpcMethodKind.OneWay) || hasReturn != method.HasResponsePayload)
                    throw ContractBindingWire.Violation("Request flags conflict with bound facts.");
                if (_requests.Count >= 65536) throw ContractBindingWire.Error(ContractBindStatus.Capacity);
                if (!quota.TryReserveProtectedRequest()) throw ContractBindingWire.Error(ContractBindStatus.Capacity);
                try
                {
                    var request = new ServerBindingRequest(this, entry, requestId, service, method, quota, admission, connection,
                        (flags & ProtocolV2FrameFlags.Compressed) != 0);
                    if (!_requests.TryAdd(requestId, request)) throw ContractBindingWire.Violation("Request ID is already active.");
                    try { entry.Readers.Add(request); }
                    catch { _requests.Remove(requestId); throw; }
                    return request;
                }
                catch { quota.ReturnProtectedRequest(); throw; }
            }
        }
    }
    internal ServerBindingRequest GetRequest(long requestId)
    { lock (_gate) return _requests.TryGetValue(requestId, out var request) ? request : throw ContractBindingWire.Error(ContractBindStatus.UnknownBinding); }
    internal void RemoveRequest(long requestId) { lock (_gate) _requests.Remove(requestId); }
    internal void Release(ulong token)
    {
        ServerBindingEntry? entry;
        lock (_gate) { entry = _slots[(uint)token - 1]; if (entry?.Token != token) return; }
        entry.Retire();
    }
    internal void Close()
    {
        ServerBindingEntry?[] entries;
        lock (_gate) { if (_closed) return; _closed = true; entries = [.. _slots]; }
        foreach (var entry in entries) entry?.Retire();
    }
}
