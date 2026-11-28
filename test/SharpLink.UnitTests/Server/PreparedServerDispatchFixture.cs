using System.Buffers.Binary;
using System.Reflection;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

// Unit fixture: install matching method facts and a real binding in the fixture's Ready
// session, then enter the same request dispatcher that receives frames in production.
// Transport integration tests separately cover the Bind exchange.
internal sealed class PreparedServerDispatchFixture
{
    private delegate Task RequestEntry(
        SharpLinkServer server, ServerConnectionState connection, long requestId,
        ProtocolV2FrameFlags flags, ReadOnlySequence<byte> payload,
        StripedLongMap<ServerCallCancellationState> calls, CancellationToken serverLoopToken, int oneWayInputCount);

    private delegate ValueTask OneWayEntry(
        SharpLinkServer server, ServerConnectionState connection, long requestId,
        ProtocolV2FrameFlags flags, ReadOnlySequence<byte> payload,
        StripedLongMap<ServerCallCancellationState> calls, CancellationToken serverLoopToken,
        AdmissionProgram? program, ServerRequestEnvelope request, in SharpLinkServer.DispatchPreparation preparation,
        NStudyServerBindings.Use binding, int declaredClientStreamCount,
        ServerCallCancellationState? admittedCallState, bool admissionGranted, int admittedClientStreamCount,
        ServerRetainedAdmissionPayload? retainedPayload);

    private static readonly RequestEntry SDispatch = FindEntry<RequestEntry>("DispatchRequestAsync");
    private static readonly OneWayEntry SDispatchOneWay = FindEntry<OneWayEntry>("DispatchOneWayRpc");
    private readonly SharpLinkServer _server;
    private readonly ServerConnectionState _connection;
    private readonly long _binding;
    private readonly long _methodId;

    internal PreparedServerDispatchFixture(SharpLinkServer server, ServerConnectionState connection,
        ServiceRegistration registration, long methodId)
    {
        _server = server;
        _connection = connection;
        _methodId = methodId;
        if (!registration.Stub.TryPrepareMethod(methodId, out var prepared))
            throw new InvalidOperationException("The fixture does not expose its expected method.");

        PrepareRegistration(registration, prepared.Descriptor);
        if (!connection.NStudyBindings.TryInstall(registration, out _binding))
            throw new InvalidOperationException("The fixture could not install its prepared Session binding.");
    }

    internal static void PrepareRegistration(ServiceRegistration registration, RpcMethodDescriptor method)
    {
        var contract = new SharpLinkGeneratedContractDescriptor(
            registration.ContractType, registration.ContractType.FullName!, method.ContractId, "fixture",
            [new("Invoke", method.MethodId, method.Kind, method.SupportsCancellation, "test", "test", "fixture", method)],
            static (_, _) => throw new NotSupportedException("A server-only fixture has no proxy."),
            _ => registration.Stub);
        registration.PrepareNStudyMethods(new FixtureManifest(contract), contract);
    }

    internal Task Dispatch(long requestId, ProtocolV2FrameFlags flags = ProtocolV2FrameFlags.None,
        CancellationToken serverLoopToken = default, TimeSpan? timeBudget = null)
        => DispatchPreparedPayload(requestId, CreatePayload(timeBudget), flags, serverLoopToken);

    internal Task DispatchPreparedPayload(long requestId, byte[] payload,
        ProtocolV2FrameFlags flags = ProtocolV2FrameFlags.None, CancellationToken serverLoopToken = default)
    {
        BinaryPrimitives.WriteInt64LittleEndian(payload, _binding);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(sizeof(long)), _methodId);
        return DispatchRequestPayload(_server, _connection, requestId, flags, new(payload), serverLoopToken);
    }

    internal static Task DispatchRequestPayload(SharpLinkServer server, ServerConnectionState connection, long requestId,
        ProtocolV2FrameFlags flags, ReadOnlySequence<byte> payload, CancellationToken serverLoopToken = default)
        => SDispatch(server, connection, requestId, flags, payload, connection.CallCancellations, serverLoopToken, 0);

    // Only the observer controls enter under a caller-managed Request scope. This borrows the
    // real binding for that test and retains it through completion, as the request entry does.
    internal async ValueTask DispatchOneWayInsideCallerScope(long requestId)
    {
        var preparation = _connection.NStudyBindings.BorrowDispatch(_binding, _methodId, out var use);
        using (use)
        {
            if (!preparation.IsBound || !preparation.HasMethod)
                throw new InvalidOperationException("The fixture lost the prepared binding or method.");

            var request = new ServerRequestEnvelope(_binding, _methodId, default, default, null);
            await SDispatchOneWay(_server, _connection, requestId, ProtocolV2FrameFlags.OneWay,
                ReadOnlySequence<byte>.Empty, _connection.CallCancellations, default,
                null, request, in preparation, use, 0, null, false, 0, null).ConfigureAwait(false);
        }
    }

    private static byte[] CreatePayload(TimeSpan? timeBudget)
    {
        var payload = new byte[ProtocolV2Constants.RequestPrefixBytes + (timeBudget.HasValue ? sizeof(long) : 0)];
        if (timeBudget is { } budget)
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(ProtocolV2Constants.RequestPrefixBytes), budget.Ticks);
        return payload;
    }

    private static T FindEntry<T>(string name) where T : Delegate
        => (typeof(SharpLinkServer).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"The server fixture could not find {name}.")).CreateDelegate<T>();

    private sealed class FixtureManifest(SharpLinkGeneratedContractDescriptor contract) : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "prepared-server-fixture";
        public Assembly OwnerAssembly => typeof(PreparedServerDispatchFixture).Assembly;
        public RpcHash128 RpcAssemblyHash => new(1, 1);
        public string CompileTimeDescriptor => "prepared-server-fixture";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [contract];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }
}
