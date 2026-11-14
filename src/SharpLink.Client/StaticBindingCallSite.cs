#if SHARPLINK_775_STATIC_SLICE
namespace SharpLink.Client;

internal sealed partial class StaticBindingCallSite(SharpLinkClient owner, RpcLocalContract local, StaticBindingScope scope = default,
    SharpLinkDynamicModule? localModule = null)
{
    internal SharpLinkDynamicModule? LocalModule { get; } = localModule;
    internal void EnsureLocalRunning()
    {
        if (LocalModule is { State: not SharpLinkDynamicModuleState.Running })
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "RPC module is draining");
    }
    internal StaticBindingScope Scope { get; } = scope;
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
        var expectsInput = kind is RpcMethodKind.ClientStreaming or RpcMethodKind.DuplexStreaming ||
            kind == RpcMethodKind.OneWay && method.HasClientStreams;
        if (method.Kind != kind || method.HasClientStreams != expectsInput ||
            (expectsInput ? method.ClientStreamCount is < 1 or > 127 : method.ClientStreamCount != 0))
            throw StaticBindingWire.Error(StaticBindingFailure.UnsupportedShape);
    }
}

// A token is never separated from the physical connection that issued it.
internal readonly record struct StaticBoundTarget(ClientConnection Connection, ulong Token);
#endif
