namespace SharpLink.Server;

// Used only while dispatch owns a strong reference to the selected registration.
// Queued waits carry QueuedServiceSelection plus scalar-only Method facts instead.
internal readonly record struct ResolvedMethodCall(ServiceRegistration Registration, RpcMethodFacts Method)
{
    internal IRpcStub Stub => Registration.Stub;
    internal long ContractId => Method.ContractId;
    internal long MethodHash => Method.MethodId;
    internal bool IsDynamicModule => Registration.HasModule;
    internal CancellationToken ModuleCancellation => Registration.ModuleCancellation;
    internal int ClientStreamCount => Method.ClientStreamCount;
}
