namespace SharpLink.Runtime;

internal static class ContractMethodFacts
{
    internal static bool IsValid(in RpcMethodDescriptor m, long contractId)
    {
        if (contractId == 0 || m.ContractId != contractId || m.MethodId == 0 || !Enum.IsDefined(m.Kind) ||
            m.ClientStreamCount is < 0 or > 127 || m.HasClientStreams != (m.ClientStreamCount != 0)) return false;
        if (!m.HasResponsePayload && m.ResponseNullable) return false;
        if ((m.HasClientStreams || m.Kind is RpcMethodKind.ServerStreaming or RpcMethodKind.DuplexStreaming) && !m.SupportsCancellation) return false;
        return m.Kind switch
        {
            RpcMethodKind.Unary => !m.HasClientStreams,
            RpcMethodKind.OneWay => !m.HasResponsePayload,
            RpcMethodKind.ClientStreaming => m.HasClientStreams,
            RpcMethodKind.ServerStreaming => !m.HasClientStreams && m.HasResponsePayload,
            RpcMethodKind.DuplexStreaming => m.HasClientStreams && m.HasResponsePayload,
            _ => false
        };
    }
    internal static bool IsStaticSlice(in RpcMethodDescriptor m)
        => m.Kind == RpcMethodKind.Unary || m.Kind == RpcMethodKind.OneWay && m.ClientStreamCount == 0;
}
