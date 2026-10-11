using System.Buffers.Binary;
using System.Collections.Frozen;
using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SharpLink.Server;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Server;

public sealed class ContractAdmissionDispatchTests
{
    private static readonly RpcHash128 RegisteredIdentity = new(11, 22);

    [Test]
    [Arguments(SharpLinkServiceLifetime.Singleton, false)]
    [Arguments(SharpLinkServiceLifetime.Connection, false)]
    [Arguments(SharpLinkServiceLifetime.Call, false)]
    [Arguments(SharpLinkServiceLifetime.Singleton, true)]
    [Arguments(SharpLinkServiceLifetime.Connection, true)]
    [Arguments(SharpLinkServiceLifetime.Call, true)]
    public async Task MismatchMustRejectBeforeServiceActivationOrBusinessDecode(SharpLinkServiceLifetime lifetime, bool oneWay)
    {
        await using var harness = new Harness(lifetime);
        await harness.Dispatch(new(12, new RpcHash128(11, 23)), oneWay);
        Ensure(harness.Activations == 0 && harness.Stub.Invocations == 0, "mismatched identity must not activate or decode");
        if (oneWay) Ensure(!harness.Session.IsConnected, "one-way mismatch cannot trust stream shape");
        else await harness.AssertFailure(SharpLinkErrorCode.FailedPrecondition);
    }

    [Test]
    public async Task MismatchMustRejectBeforeCompressedEnvelopeOrPersistentDecodeSelection()
    {
        await using var harness = new Harness(SharpLinkServiceLifetime.Singleton);
        // No compressed length/body follows the identity. Contract rejection must win before
        // parsing those fields, selecting a persistent worker, or running a business codec.
        await harness.Dispatch(new(12, new RpcHash128(11, 23)), compressed: true);
        await harness.AssertFailure(SharpLinkErrorCode.FailedPrecondition);
        Ensure(harness.Activations == 0 && harness.Stub.Invocations == 0 &&
            harness.Server.DecodeStartedWorkCountForDiagnostics == 0, "no decode work admitted");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DynamicSingletonMismatchMustNotAcquireOrActivateModule(bool retiring)
    {
        await using var harness = new Harness(SharpLinkServiceLifetime.Singleton, dynamic: true);
        if (retiring) harness.Module!.TryBeginDraining();
        await harness.Dispatch(new(12, new RpcHash128(11, 23)));
        await harness.AssertFailure(SharpLinkErrorCode.FailedPrecondition);
        Ensure(harness.Module!.RemainingCalls == 0 && harness.Activations == 0 && harness.Stub.Invocations == 0,
            "mismatch cannot acquire a module lease or enter user code");
    }

    [Test]
    public async Task MatchingRetiredDynamicRegistrationMustStillRejectAtModuleBoundary()
    {
        await using var harness = new Harness(SharpLinkServiceLifetime.Singleton, dynamic: true);
        harness.Module!.TryBeginDraining();
        await harness.Dispatch(new(12, RegisteredIdentity));
        await harness.AssertFailure(SharpLinkErrorCode.Unavailable);
        Ensure(harness.Activations == 0 && harness.Stub.Invocations == 0, "identity cannot bypass module retirement");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MissingOrUnnegotiatedIdentityMustFailClosed(bool unnegotiated)
    {
        await using var harness = new Harness(SharpLinkServiceLifetime.Singleton, negotiateAdmission: !unnegotiated);
        try
        {
            await harness.Dispatch(new(12, RegisteredIdentity), includeIdentity: unnegotiated);
            throw new Exception("Expected missing or unnegotiated identity rejection.");
        }
        catch (TargetInvocationException exception) when (
            exception.InnerException is SharpLinkException { Code: SharpLinkErrorCode.ProtocolViolation })
        { }
        Ensure(harness.Activations == 0 && harness.Stub.Invocations == 0, "missing identity cannot invoke");
    }

    [Test]
    [Arguments(SharpLinkServiceLifetime.Singleton)]
    [Arguments(SharpLinkServiceLifetime.Connection)]
    [Arguments(SharpLinkServiceLifetime.Call)]
    public async Task MatchingIdentityShouldReachSelectedRegistration(SharpLinkServiceLifetime lifetime)
    {
        await using var harness = new Harness(lifetime);
        await harness.Dispatch(new(12, RegisteredIdentity));
        Ensure(harness.Activations == 1 && harness.Stub.Invocations == 1, "matching identity admits every lifetime");
    }

    [Test]
    [Arguments(ContractRegistrationMatch.MinimumVersion)]
    [Arguments(ContractRegistrationMatch.ExactRegistration)]
    public async Task VersionedRequirementMustRejectAnotherServerOrEpoch(ContractRegistrationMatch match)
    {
        await using var harness = new Harness(SharpLinkServiceLifetime.Singleton);
        await harness.Dispatch(new(harness.Receipt with { ServerInstanceId = Guid.NewGuid() }, match));
        await harness.AssertFailure(SharpLinkErrorCode.FailedPrecondition);
        await harness.Dispatch(new(harness.Receipt with { RegistryEpoch = Guid.NewGuid() }, match));
        await harness.AssertFailure(SharpLinkErrorCode.FailedPrecondition);
        Ensure(harness.Activations == 0 && harness.Stub.Invocations == 0, "timeline scope precedes user entry");
    }

    [Test]
    public async Task ExactRegistrationMustRejectSameHashDifferentInstanceWhileMinimumVersionAcceptsIt()
    {
        await using var harness = new Harness(SharpLinkServiceLifetime.Singleton);
        var oldReceipt = harness.Receipt with { RegistrationId = Guid.NewGuid() };
        await harness.Dispatch(new(oldReceipt, ContractRegistrationMatch.ExactRegistration));
        await harness.AssertFailure(SharpLinkErrorCode.FailedPrecondition);
        Ensure(harness.Activations == 0, "same hash is insufficient for exact registration");
        await harness.Dispatch(new(oldReceipt, ContractRegistrationMatch.MinimumVersion));
        Ensure(harness.Activations == 1 && harness.Stub.Invocations == 1, "compatible registration meets minimum generation");
    }

    [Test]
    public async Task GenerationFloorMustRejectBeforeActivation()
    {
        await using var harness = new Harness(SharpLinkServiceLifetime.Call);
        await harness.Dispatch(new(harness.Receipt with { Generation = 1 }, ContractRegistrationMatch.MinimumVersion));
        await harness.AssertFailure(SharpLinkErrorCode.FailedPrecondition);
        Ensure(harness.Activations == 0 && harness.Stub.Invocations == 0, "future receipt rejected");
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private readonly ServiceProvider _provider = new ServiceCollection().BuildServiceProvider();
        private readonly RpcGeneratedManifestRegistration? _codecs;
        private readonly ServerConnectionState _connection;
        private long _requestId;
        internal int Activations { get; private set; }
        internal SharpLinkServer Server { get; }
        internal RpcSession Session { get; }
        internal CountingStub Stub { get; } = new();
        internal SharpLinkDynamicModule? Module { get; }
        internal ContractRegistrationReceipt Receipt { get; }

        internal Harness(SharpLinkServiceLifetime lifetime, bool dynamic = false, bool negotiateAdmission = true)
        {
            Server = (SharpLinkServer)SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                .DisableAutomaticServiceRegistration().UseTransport(new IdleListener()).Build();
            var context = (SharpLinkRuntimeContext)typeof(SharpLinkServer)
                .GetField("_runtimeContext", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Server)!;
            Session = RpcSessionTestFixture.CreateSessionOverTestTransport("contract-admission", _input.Reader, _output.Writer,
                RpcSessionTestFixture.ServerOptions(context), completeHandshake: false);
            RpcSessionTestFixture.CompleteHandshake(Session, negotiateAdmission ? ProtocolV2Capabilities.ContractAdmission : ProtocolV2Capabilities.None);
            _connection = new ServerConnectionState(Session, new RpcSessionGeneratedServerBridge(Session),
                new StripedLongMap<ServerCallCancellationState>(context.Concurrency), CancellationToken.None, context.TimeProvider);
            Ensure(_connection.MarkReady(null), "connection ready");
            if (dynamic)
            {
                var manifest = new EmptyManifest();
                _codecs = context.PrepareGeneratedManifest(manifest);
                Module = new SharpLinkDynamicModule(manifest.OwnerAssembly, manifest, _codecs);
            }
            var definition = new ServiceRegistrationDefinition(typeof(object), Stub, lifetime,
                _ => { Activations++; return new object(); }, null, false);
            var registration = definition.Build(_provider, Module, RegisteredIdentity);
            var registry = (ServerServiceModuleRegistry)typeof(SharpLinkServer)
                .GetField("_serviceModuleRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Server)!;
            registry.PublishServices(new Dictionary<long, ServiceRegistration> { [12] = registration }.ToFrozenDictionary());
            Receipt = new(registry.ServerInstanceId, registry.RegistryEpoch, 0, 12, RegisteredIdentity, registration.RegistrationId);
            typeof(SharpLinkServer).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Server, 2);
        }

        internal ValueTask Dispatch(ContractRequirement requirement, bool oneWay = false, bool compressed = false, bool includeIdentity = true)
        {
            var bytes = new byte[16 + ProtocolV2ContractAdmissionCodec.GetPayloadLength(requirement)];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, 12);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), 34);
            ProtocolV2ContractAdmissionCodec.Write(bytes.AsSpan(16), requirement);
            var flags = includeIdentity ? ProtocolV2FrameFlags.HasContractIdentity : ProtocolV2FrameFlags.None;
            if (compressed) flags |= ProtocolV2FrameFlags.Compressed | ProtocolV2FrameFlags.Cancellable;
            if (oneWay) flags |= ProtocolV2FrameFlags.OneWay;
            var method = typeof(SharpLinkServer).GetMethod(oneWay ? "DispatchOneWayRpc" : "DispatchRpcAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var id = ++_requestId;
            object?[] arguments = oneWay
                ? [_connection, id, flags, new ReadOnlySequence<byte>(bytes), _connection.CallCancellations,
                    CancellationToken.None, null, null, false, 0, null]
                : [_connection, id, flags, new ReadOnlySequence<byte>(bytes), _connection.CallCancellations,
                    CancellationToken.None, null, null, false, null];
            return (ValueTask)method.Invoke(Server, arguments)!;
        }

        internal async Task AssertFailure(SharpLinkErrorCode code)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var read = await _output.Reader.ReadAsync(timeout.Token);
            var buffer = read.Buffer;
            try
            {
                Ensure(ProtocolV2FrameParser.TryReadFrame(ref buffer, new(), out var header, out var payload), "error frame available");
                Ensure(header.Type == ProtocolV2FrameType.Response && (header.Flags & ProtocolV2FrameFlags.Error) != 0, "unary error response");
                var error = ProtocolV2PayloadCodec.ReadError(payload, header.Flags, new SharpLinkProtocolOptions().MaxErrorMessageBytes);
                Ensure(error.Code == code, "precise contract rejection code");
            }
            finally { _output.Reader.AdvanceTo(read.Buffer.End); }
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.CloseAsync();
            await Server.DisposeAsync();
            _codecs?.Dispose();
            await _provider.DisposeAsync();
            await _input.Writer.CompleteAsync();
            await _output.Reader.CompleteAsync();
        }
    }

    private sealed class CountingStub : IRpcStub
    {
        internal int Invocations { get; private set; }
        public long InterfaceHash => 12;
        public bool TryGetMethodDescriptor(long methodHash, out RpcMethodDescriptor descriptor)
        {
            descriptor = new(12, methodHash, RpcMethodKind.Unary, false, false, false, null);
            return true;
        }
        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash, long requestId,
            ReadOnlySequence<byte> args)
        { Invocations++; return ValueTask.CompletedTask; }
        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash, long requestId,
            ReadOnlySequence<byte> args, CancellationToken cancellationToken) => InvokeNoReturnAsync(service, bridge, methodHash, requestId, args);
        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash, long requestId,
            ReadOnlySequence<byte> args, IBufferWriter<byte> output) => InvokeNoReturnAsync(service, bridge, methodHash, requestId, args);
        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash, long requestId,
            ReadOnlySequence<byte> args, IBufferWriter<byte> output, CancellationToken cancellationToken) => InvokeNoReturnAsync(service, bridge, methodHash, requestId, args);
    }

    private sealed class EmptyManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(ContractAdmissionDispatchTests).Assembly;
        public RpcHash128 RpcAssemblyHash => RegisteredIdentity;
        public string CompileTimeDescriptor => "test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class IdleListener : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => null;
        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
