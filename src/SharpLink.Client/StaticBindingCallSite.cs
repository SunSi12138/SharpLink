#if SHARPLINK_775_STATIC_SLICE
namespace SharpLink.Client;

internal sealed partial class StaticBindingCallSite(SharpLinkClient owner, RpcLocalContract local)
{
    internal SharpLinkClient Owner { get; } = owner;
    internal RpcLocalContract Local { get; } = local;
    internal long ContractId => Local.ContractId;
    internal RpcHash128 Hash => Local.Hash;
    internal StaticBoundTarget[] Snapshot = [];
    internal Exception? Failure;
    internal int RefreshState;
    internal int Constructions;
    internal bool DemandCommitted;

    internal void Validate(RpcMethodDescriptor method, RpcMethodKind kind)
    {
        Owner.EnsureStaticSliceAvailable();
        if (method.ContractId != ContractId)
            throw StaticBindingWire.Error(StaticBindingFailure.HashMismatch);
        if (method.Kind != kind || method.HasClientStreams || method.ClientStreamCount != 0)
            throw StaticBindingWire.Error(StaticBindingFailure.UnsupportedShape);
    }
}

// A token is never separated from the physical connection that issued it.
internal readonly record struct StaticBoundTarget(ClientConnection Connection, ulong Token);
#endif
