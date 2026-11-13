#if SHARPLINK_775_STATIC_SLICE
namespace SharpLink.Runtime;

// Cold validation only. Dispatch consumes a value already validated under registration ownership.
internal static class ResolvedMethodValidation
{
    internal static bool IsValid(in RpcMethodDescriptor method, long contractId, long methodId)
    {
        if (method.ContractId != contractId || method.MethodId != methodId ||
            method.Kind is < RpcMethodKind.Unary or > RpcMethodKind.DuplexStreaming ||
            method.ClientStreamCount is < 0 or > 127 || method.HasClientStreams != (method.ClientStreamCount != 0) ||
            method.HasClientStreams && !method.SupportsCancellation) return false;
        return method.Kind switch
        {
            RpcMethodKind.Unary => !method.HasClientStreams,
            RpcMethodKind.OneWay => !method.HasResponsePayload,
            RpcMethodKind.ClientStreaming => method.HasClientStreams && method.SupportsCancellation,
            RpcMethodKind.ServerStreaming => !method.HasClientStreams && method.HasResponsePayload && method.SupportsCancellation,
            RpcMethodKind.DuplexStreaming => method.HasClientStreams && method.HasResponsePayload && method.SupportsCancellation,
            _ => false
        };
    }
}
#endif
