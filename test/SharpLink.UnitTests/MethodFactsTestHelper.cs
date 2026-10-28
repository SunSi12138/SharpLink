namespace SharpLink.UnitTests;

internal static class MethodFactsTestHelper
{
    internal static RpcMethodFacts FromDescriptor(RpcMethodDescriptor descriptor, bool supportsCancellation = true)
        => new(descriptor.ContractId, descriptor.MethodId, descriptor.Kind, descriptor.ClientStreamCount,
            supportsCancellation, descriptor.HasResponsePayload, descriptor.ResponseNullable,
            descriptor.HasMethodTimeout, descriptor.MethodTimeout, descriptor.IsIdempotent);
}
