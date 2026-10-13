using System.Collections.Frozen;
using System.Reflection;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class ServerContractAdmissionTests
{
    private const long ContractId = 775;
    private const long MethodId = 17;
    private static readonly RpcHash128 Hash = new(0x775, 0x2026);

    [Test]
    [Arguments(RpcMethodKind.Unary, 0)]
    [Arguments(RpcMethodKind.OneWay, 0)]
    [Arguments(RpcMethodKind.OneWay, 1)]
    [Arguments(RpcMethodKind.ClientStreaming, 1)]
    [Arguments(RpcMethodKind.ServerStreaming, 0)]
    [Arguments(RpcMethodKind.DuplexStreaming, 2)]
    public void WrongAssemblyIdentityMustRejectBeforeAnyGeneratedCode(RpcMethodKind kind, int streamCount)
    {
        var stub = new AdmissionStub(kind, streamCount) { RejectDescriptorAccess = true };
        var registration = CreateRegistration(stub);
        var registry = CreateRegistry(registration);
        var identity = new ProtocolV2RequestIdentity(
            ContractId, MethodId, new RpcHash128(12, 34), kind, checked((ushort)streamCount));

        Ensure(!ServerContractAdmission.TryAcquire(registry, in identity, Flags(kind),
                out _, out var lease, out var error), "incompatible request must fail admission");
        Ensure(!lease.IsAcquired, "rejected admission must not own a module lease");
        Ensure(error is
            {
                Code: SharpLinkErrorCode.FailedPrecondition,
                DetailCode: SharpLinkErrorDetails.FailedPrecondition.ContractMismatch
            },
            "wire mismatch must have a stable structured failure");
        Ensure(stub.DescriptorReads == 0 && stub.Invocations == 0,
            "incompatible code must remain untouched for every invocation shape");
    }

    [Test]
    [Arguments(RpcMethodKind.ServerStreaming, 0)]
    [Arguments(RpcMethodKind.DuplexStreaming, 1)]
    public void StreamReturnPayloadMustNotRequireTheUnaryHasReturnFlag(RpcMethodKind kind, int streamCount)
    {
        var registration = CreateRegistration(new AdmissionStub(kind, streamCount, responsePayload: true));
        var registry = CreateRegistry(registration);
        var identity = new ProtocolV2RequestIdentity(ContractId, MethodId, Hash, kind, checked((ushort)streamCount));
        Ensure(ServerContractAdmission.TryAcquire(registry, in identity, ProtocolV2FrameFlags.Cancellable,
            out _, out var lease, out _), "stream elements travel outside the final response frame");
        lease.Dispose();
    }

    [Test]
    public void ReceiptRequirementsMustUseOneServerTimelineAndRegistrationIdentity()
    {
        var registration = CreateRegistration(new AdmissionStub(RpcMethodKind.Unary));
        var registry = CreateRegistry(registration);
        var current = PublishReceipt(registry, registration, generation: 10);
        var earlier = current with { Generation = 9, RegistrationId = Guid.NewGuid() };

        Ensure(Admits(registry, earlier, RpcRegistrationRequirement.Compatible),
            "compatible receipt accepts the current same-hash registration");
        Ensure(Admits(registry, earlier, RpcRegistrationRequirement.MinimumVersion),
            "minimum version compares generations only within the same timeline");
        Ensure(!Admits(registry, earlier, RpcRegistrationRequirement.ExactRegistration),
            "same hash does not satisfy a different exact registration");
        Ensure(Admits(registry, current, RpcRegistrationRequirement.ExactRegistration),
            "the current exact registration must match");
        Ensure(!Admits(registry, current with { Generation = 11 }, RpcRegistrationRequirement.MinimumVersion),
            "a receipt newer than the current service registration must not match");

        foreach (var requirement in Enum.GetValues<RpcRegistrationRequirement>())
        {
            Ensure(!Admits(registry, current with { ServerInstanceId = Guid.NewGuid() }, requirement),
                "a receipt cannot cross a restart or another server instance");
            Ensure(!Admits(registry, current with { RegistryEpoch = Guid.NewGuid() }, requirement),
                "a receipt cannot cross registry epochs");
            Ensure(!Admits(registry, current with { ContractId = ContractId + 1 }, requirement),
                "a receipt cannot cross contract scope");
        }
    }

    [Test]
    public async Task AdmittedDynamicCallMustKeepTheExactRegistrationAcrossReplacement()
    {
        using var runtime = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var manifest = new AdmissionManifest();
        using var oldCodecs = runtime.PrepareGeneratedManifest(manifest);
        using var newCodecs = runtime.PrepareGeneratedManifest(manifest);
        var oldModule = new SharpLinkDynamicModule(manifest.OwnerAssembly, manifest, oldCodecs);
        var newModule = new SharpLinkDynamicModule(manifest.OwnerAssembly, manifest, newCodecs);
        var oldRegistration = CreateRegistration(new AdmissionStub(RpcMethodKind.ServerStreaming), oldModule);
        var replacement = CreateRegistration(new AdmissionStub(RpcMethodKind.ServerStreaming), newModule);
        var registry = CreateRegistry(oldRegistration);
        var receipt = PublishReceipt(registry, oldRegistration, generation: 1);
        PublishReceipt(registry, replacement, generation: 2);
        var identity = new ProtocolV2RequestIdentity(
            ContractId, MethodId, Hash, RpcMethodKind.ServerStreaming, 0, receipt,
            RpcRegistrationRequirement.ExactRegistration);

        Ensure(ServerContractAdmission.TryAcquire(registry, in identity, ProtocolV2FrameFlags.None,
                out var admitted, out var lease, out _), "old registration must first admit");
        try
        {
            lock (registry.Gate)
            {
                registry.ServicesStorage = new Dictionary<long, ServiceRegistration>
                {
                    [ContractId] = replacement
                }.ToFrozenDictionary();
                oldModule.TryBeginDraining();
            }
            Ensure(ReferenceEquals(admitted, oldRegistration), "admitted stream cannot retarget a new registration");
            Ensure(oldModule.RemainingCalls == 1 && oldModule.RemainingStreams == 1,
                "admission must pin the stream before business decode or activation");
            Ensure(!oldModule.WaitForDrainAsync().IsCompleted, "replacement must wait for the admitted stream lease");
            Ensure(admitted.TryAcquireDynamicSingleton(false, null, 1, out _, out var activationLease,
                    moduleLeaseRetained: true), "already admitted activation can use its old registration");
            Ensure(!activationLease.IsAcquired && oldModule.RemainingCalls == 1,
                "activation must not race a second module acquisition after retirement");
            Ensure(!ServerContractAdmission.TryAcquire(registry, in identity, ProtocolV2FrameFlags.None,
                    out _, out var rejectedLease, out var rejection), "new exact calls must reject the replaced registration");
            Ensure(!rejectedLease.IsAcquired && rejection is
                {
                    Code: SharpLinkErrorCode.FailedPrecondition,
                    DetailCode: SharpLinkErrorDetails.FailedPrecondition.ReceiptMismatch
                },
                "stale exact registration must reject before lease acquisition");
        }
        finally
        {
            lease.Dispose();
            await oldRegistration.DisposeAsync();
            await replacement.DisposeAsync();
        }
        Ensure(oldModule.RemainingCalls == 0 && oldModule.RemainingStreams == 0 &&
               oldModule.WaitForDrainAsync().IsCompleted,
            "completed admission must release every old-generation resource");
    }

    [Test]
    public void RetirementAndUnregisterMustFailWithoutFallingBackToAnotherRegistration()
    {
        using var runtime = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var manifest = new AdmissionManifest();
        using var codecs = runtime.PrepareGeneratedManifest(manifest);
        var module = new SharpLinkDynamicModule(manifest.OwnerAssembly, manifest, codecs);
        var registration = CreateRegistration(new AdmissionStub(RpcMethodKind.Unary), module);
        var registry = CreateRegistry(registration);
        PublishReceipt(registry, registration, generation: 1);
        var identity = new ProtocolV2RequestIdentity(ContractId, MethodId, Hash, RpcMethodKind.Unary, 0);
        module.TryBeginDraining();

        Ensure(!ServerContractAdmission.TryAcquire(registry, in identity, ProtocolV2FrameFlags.None,
                out _, out _, out var retiring), "draining registry must fail admission");
        Ensure(retiring is
            {
                Code: SharpLinkErrorCode.Unavailable,
                DetailCode: SharpLinkErrorDetails.Unavailable.RegistrationRetired
            },
            "lease failure must have a structured retirement error");
        lock (registry.Gate)
            registry.ServicesStorage = FrozenDictionary<long, ServiceRegistration>.Empty;
        Ensure(!ServerContractAdmission.TryAcquire(registry, in identity, ProtocolV2FrameFlags.None,
                out _, out _, out var missing), "removed registry must fail admission");
        Ensure(missing is
            {
                Code: SharpLinkErrorCode.Unimplemented,
                DetailCode: SharpLinkErrorDetails.Unimplemented.ContractNotRegistered
            },
            "unregistered contract has a distinct stable failure");
        Ensure(module.RemainingCalls == 0, "neither rejection may retain the retired module");
    }

    [Test]
    public void MethodAndWireShapeMustBeCheckedBeforeActivation()
    {
        var stub = new AdmissionStub(RpcMethodKind.Unary);
        var registration = CreateRegistration(stub);
        var registry = CreateRegistry(registration);
        var unknown = new ProtocolV2RequestIdentity(ContractId, MethodId + 1, Hash, RpcMethodKind.Unary, 0);
        Ensure(!ServerContractAdmission.TryAcquire(registry, in unknown, ProtocolV2FrameFlags.None,
                out _, out _, out var missing), "unknown method cannot activate a service");
        Ensure(missing is
            {
                Code: SharpLinkErrorCode.Unimplemented,
                DetailCode: SharpLinkErrorDetails.Unimplemented.MethodNotImplemented
            },
            "unknown method needs a stable detail code");
        var forgedShape = unknown with { MethodId = MethodId, Kind = RpcMethodKind.OneWay };
        try
        {
            ServerContractAdmission.TryAcquire(registry, in forgedShape, ProtocolV2FrameFlags.OneWay,
                out _, out _, out _);
            throw new Exception("a mismatching invocation shape must fail closed");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation)
        {
        }
        Ensure(stub.Invocations == 0, "neither method rejection can invoke business code");
    }

    private static bool Admits(ServerServiceModuleRegistry registry, ContractRegistrationReceipt receipt,
        RpcRegistrationRequirement requirement)
    {
        var identity = new ProtocolV2RequestIdentity(
            ContractId, MethodId, Hash, RpcMethodKind.Unary, 0, receipt, requirement);
        var accepted = ServerContractAdmission.TryAcquire(registry, in identity, ProtocolV2FrameFlags.None,
            out _, out var lease, out var rejection);
        lease.Dispose();
        if (!accepted)
            Ensure(rejection is { DetailCode: SharpLinkErrorDetails.FailedPrecondition.ReceiptMismatch },
                "receipt rejection must be structured");
        return accepted;
    }

    private static ProtocolV2FrameFlags Flags(RpcMethodKind kind)
        => kind == RpcMethodKind.OneWay ? ProtocolV2FrameFlags.OneWay : ProtocolV2FrameFlags.None;

    private static ServiceRegistration CreateRegistration(AdmissionStub stub, SharpLinkDynamicModule? module = null)
    {
        var registration = ServiceRegistration.CreateSingleton(
            typeof(ServerContractAdmissionTests), stub, new object(), ownsService: false, module);
        registration.RpcAssemblyHash = Hash;
        return registration;
    }

    private static ServerServiceModuleRegistry CreateRegistry(ServiceRegistration registration)
        => new(new Dictionary<long, ServiceRegistration> { [ContractId] = registration }.ToFrozenDictionary());

    private static ContractRegistrationReceipt PublishReceipt(ServerServiceModuleRegistry registry,
        ServiceRegistration registration, ulong generation)
    {
        var receipt = new ContractRegistrationReceipt(registry.ServerInstanceId, registry.RegistryEpoch,
            generation, ContractId, Hash, Guid.NewGuid());
        registration.PublishReceipt(receipt);
        return receipt;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class AdmissionStub(RpcMethodKind kind, int streamCount = 0, bool responsePayload = false) : IRpcStub
    {
        internal bool RejectDescriptorAccess { get; init; }
        internal int DescriptorReads { get; private set; }
        internal int Invocations { get; private set; }
        public long InterfaceHash => ContractId;

        public bool TryGetMethodDescriptor(long methodHash, out RpcMethodDescriptor descriptor)
        {
            if (RejectDescriptorAccess)
                throw new Exception("an incompatible generated stub was accessed");
            DescriptorReads++;
            descriptor = new RpcMethodDescriptor(ContractId, MethodId, kind, responsePayload,
                streamCount != 0, false, null, ClientStreamCount: streamCount);
            return methodHash == MethodId;
        }

        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args)
        {
            Invocations++;
            return ValueTask.CompletedTask;
        }

        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge,
            long methodHash, long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken)
            => InvokeNoReturnAsync(service, bridge, methodHash, requestId, args);

        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output)
            => InvokeNoReturnAsync(service, bridge, methodHash, requestId, args);

        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output, CancellationToken cancellationToken)
            => InvokeNoReturnAsync(service, bridge, methodHash, requestId, args);
    }

    private sealed class AdmissionManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(ServerContractAdmissionTests).Assembly;
        public RpcHash128 RpcAssemblyHash => Hash;
        public string CompileTimeDescriptor => "contract-admission";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }
}
