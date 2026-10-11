namespace SharpLink.Server;

/// <summary>
/// Carries the immutable generated method facts resolved once for one RPC. Passing this value
/// instead of a bare method identifier is what keeps the generated fact table out of admission,
/// cancellation, stream reservation, telemetry and invocation orchestration.
/// </summary>
internal readonly struct ResolvedMethodCall
{
    private readonly ServiceRegistration _registration;

    internal ResolvedMethodCall(ServiceRegistration registration, long methodHash, RpcMethodShape shape)
    {
        _registration = registration;
        MethodHash = methodHash;
        Shape = shape;
    }

    internal ServiceRegistration Registration => _registration;

    internal IRpcStub Stub => _registration.Stub;

    internal long MethodHash { get; }

    internal RpcMethodShape Shape { get; }

    /// <summary>Gets the generated contract identifier already owned by the service registration.</summary>
    internal long ContractId => _registration.Stub.InterfaceHash;

    internal bool AcceptsCalls => _registration.AcceptsCalls;

    internal CancellationToken ModuleCancellation => _registration.ModuleCancellation;

    internal bool IsDynamicModule => _registration.Module is not null;

    internal bool SupportsCooperativeCancellation => Shape.SupportsCancellation;

    /// <summary>
    /// Gets the client-stream count to reserve. A method whose shape could not be resolved keeps the
    /// legacy "no reservation" behavior instead of trusting the unknown sentinel value.
    /// </summary>
    internal int ClientStreamCount => Shape.HasKnownClientStreamCount ? Shape.ClientStreamCount : 0;
}
