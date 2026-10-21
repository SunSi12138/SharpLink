using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.UnitTests.Runtime;

// Handwritten manifest/stub fixture, not source-generator coverage. Each registration has its own
// real Assembly/contract Type, while its logical contract ID and complete RPC hash stay identical.
// Emitted assemblies are noncollectible: these tests assert service disposal, not ALC/code collection.
public static partial class ImmutableRouteTransportSupport
{
    internal const long ContractId = 775_101;
    internal const long UnaryId = 775_102;
    internal const long OneWayId = 775_103;
    internal static readonly RpcHash128 AssemblyHash = new(775, 20261021);
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<int, ServiceState> States = [];
    private static int _nextMarker;
    internal static readonly RpcMethodDescriptor Unary = new(
        ContractId, UnaryId, RpcMethodKind.Unary, true, false, false, null);
    internal static readonly RpcMethodDescriptor OneWay = new(
        ContractId, OneWayId, RpcMethodKind.OneWay, false, false, false, null);

    internal static Plugin CreatePlugin()
    {
        var marker = Interlocked.Increment(ref _nextMarker);
        var state = new ServiceState(marker);
        Ensure(States.TryAdd(marker, state), "unique fixture marker");
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"ImmutableRouteFixture{marker}"), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("fixture");
        var contractBuilder = module.DefineType("IRouteFixture",
            TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        contractBuilder.AddInterfaceImplementation(typeof(IService));
        var contract = contractBuilder.CreateType()!;
        var serviceBuilder = module.DefineType("RouteFixtureService", TypeAttributes.Public, typeof(ServiceBase));
        serviceBuilder.AddInterfaceImplementation(contract);
        var serviceCtor = serviceBuilder.DefineConstructor(MethodAttributes.Public,
            CallingConventions.Standard, Type.EmptyTypes).GetILGenerator();
        serviceCtor.Emit(OpCodes.Ldarg_0);
        serviceCtor.Emit(OpCodes.Ldc_I4, marker);
        serviceCtor.Emit(OpCodes.Call, typeof(ServiceBase).GetConstructor([typeof(int)])!);
        serviceCtor.Emit(OpCodes.Ret);
        var service = serviceBuilder.CreateType()!;
        var manifestBuilder = module.DefineType("RouteFixtureManifest", TypeAttributes.Public, typeof(ManifestBase));
        var manifestCtor = manifestBuilder.DefineConstructor(MethodAttributes.Public,
            CallingConventions.Standard, Type.EmptyTypes).GetILGenerator();
        manifestCtor.Emit(OpCodes.Ldarg_0);
        manifestCtor.Emit(OpCodes.Ldtoken, contract);
        manifestCtor.Emit(OpCodes.Call, typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
        manifestCtor.Emit(OpCodes.Ldtoken, service);
        manifestCtor.Emit(OpCodes.Call, typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
        manifestCtor.Emit(OpCodes.Call, typeof(ManifestBase).GetConstructor([typeof(Type), typeof(Type)])!);
        manifestCtor.Emit(OpCodes.Ret);
        var manifest = manifestBuilder.CreateType()!;
        var locator = typeof(SharpLinkGeneratedAssemblyManifestAttribute)
            .GetConstructor([typeof(Type), typeof(int), typeof(int), typeof(string), typeof(string)])!;
        assembly.SetCustomAttribute(new CustomAttributeBuilder(locator,
            [manifest, SharpLinkGeneratedManifestVersions.Api, SharpLinkGeneratedManifestVersions.Protocol,
                "immutable-route-fixture", SharpLinkGeneratedManifestVersions.AbiIdentity]));
        // Attribute Type arguments resolve by assembly name; emitted assemblies are not probeable files.
        ResolveEventHandler resolver = (_, args) => args.Name == manifest.Assembly.FullName ? manifest.Assembly : null;
        AppDomain.CurrentDomain.AssemblyResolve += resolver;
        return new Plugin(manifest.Assembly, state, resolver);
    }

    internal static ValueTask<int> InvokeUnary(SharpLinkClient client, RpcMethodDescriptor method,
        IRpcCodec<RpcEmptyRequest>? codec = null)
    {
        var request = default(RpcEmptyRequest);
        return ((IRpcChannel)client).InvokeUnaryAsync(method, in request,
            codec ?? RpcEmptyRequestCodec.Instance, Int32Codec.Instance, null);
    }

    internal static ValueTask InvokeOneWay(SharpLinkClient client, RpcMethodDescriptor method)
    {
        var request = default(RpcEmptyRequest);
        var streams = default(RpcNoClientStreams);
        return ((IRpcChannel)client).InvokeOneWayAsync(method, in request,
            RpcEmptyRequestCodec.Instance, in streams, null);
    }

    internal static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!predicate())
            await Task.Delay(1, timeout.Token);
    }

    internal static async Task<Exception> CaptureFailureAsync(Task task)
    {
        try { await task.WaitAsync(Timeout); }
        catch (Exception exception) { return exception; }
        throw new Exception("Expected the call to fail, but it succeeded.");
    }

    internal static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    internal sealed record Plugin(Assembly Assembly, ServiceState State, ResolveEventHandler Resolver) : IDisposable
    {
        public void Dispose()
        {
            AppDomain.CurrentDomain.AssemblyResolve -= Resolver;
            State.Release.TrySetResult();
            States.TryRemove(State.Marker, out _);
        }
    }

    internal sealed record ServerFixture(SharpLinkServer Server, IPEndPoint Endpoint) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Server.DisposeAsync();
    }

    public sealed class ServiceState(int marker)
    {
        internal int Marker { get; } = marker;
        internal bool Hold;
        internal int Calls;
        internal int OneWayCalls;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public class ServiceBase : IAsyncDisposable
    {
        internal ServiceState State { get; }
        public ServiceBase(int marker) => State = States[marker];
        internal async ValueTask<int> InvokeAsync()
        {
            Interlocked.Increment(ref State.Calls);
            State.Entered.TrySetResult();
            if (State.Hold)
                await State.Release.Task;
            return State.Marker;
        }
        public ValueTask DisposeAsync()
        {
            State.Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    public class ManifestBase : ISharpLinkGeneratedAssemblyManifest
    {
        private const string Fingerprint = "7757757757757757757757757757757757757757757757757757757757757757";
        public ManifestBase(Type contract, Type service)
        {
            OwnerAssembly = contract.Assembly;
            Contracts = [new(contract, "RouteFixture", ContractId, Fingerprint,
                [new("Unary", UnaryId, RpcMethodKind.Unary, false, "empty", "int32", Fingerprint),
                 new("OneWay", OneWayId, RpcMethodKind.OneWay, false, "empty", "void", Fingerprint)],
                static (_, _) => throw new NotSupportedException("Direct channel fixture only."),
                static _ => new Stub())];
            Services = [new(contract, service, "RouteFixture", service.FullName!, ContractId,
                Fingerprint, SharpLinkServiceLifetime.Singleton, [],
                _ => Activator.CreateInstance(service)!)];
        }
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "immutable-route-fixture";
        public Assembly OwnerAssembly { get; }
        public RpcHash128 RpcAssemblyHash => AssemblyHash;
        public string CompileTimeDescriptor => "immutable-route-fixture";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; }
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services { get; }
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class Stub : IRpcStub
    {
        public long InterfaceHash => ContractId;
        public bool SupportsCancellation(long methodHash) => false;
        public bool TryGetMethodDescriptor(long methodHash, out RpcMethodDescriptor descriptor)
        {
            descriptor = methodHash == UnaryId ? Unary : OneWay;
            return methodHash is UnaryId or OneWayId;
        }
        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge,
            long methodHash, long requestId, ReadOnlySequence<byte> args)
        {
            Interlocked.Increment(ref ((ServiceBase)service).State.OneWayCalls);
            return ValueTask.CompletedTask;
        }
        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge,
            long methodHash, long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken)
            => InvokeNoReturnAsync(service, bridge, methodHash, requestId, args);
        public async ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge,
            long methodHash, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output)
        {
            var result = await ((ServiceBase)service).InvokeAsync();
            Int32Codec.Instance.Serialize(result, output);
        }
        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge,
            long methodHash, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output,
            CancellationToken cancellationToken)
            => InvokeAsync(service, bridge, methodHash, requestId, args, output);
    }

    private sealed class Int32Codec : IRpcCodec<int>
    {
        internal static readonly Int32Codec Instance = new();
        public void Serialize(in int value, IBufferWriter<byte> buffer)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.GetSpan(4), value);
            buffer.Advance(4);
        }
        public int Deserialize(in ReadOnlySequence<byte> buffer)
        {
            var reader = new SequenceReader<byte>(buffer);
            if (!reader.TryReadLittleEndian(out int value) || !reader.End)
                throw new InvalidDataException("Expected one int32 response.");
            return value;
        }
    }

    internal sealed class IdentityObserver : ISharpLinkClientInterceptor, ISharpLinkEndpointAdmissionPolicy
    {
        internal ConcurrentQueue<long> Intercepted { get; } = new();
        internal ConcurrentQueue<long> Admitted { get; } = new();
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context, SharpLinkClientInvocationDelegate next)
        {
            Intercepted.Enqueue(context.Method.ContractId);
            return next(context);
        }
        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint, in RpcMethodDescriptor method)
        {
            Admitted.Enqueue(method.ContractId);
            return new(method.ContractId == ContractId, 0, null);
        }
        public void Report(in SharpLinkEndpointOutcome outcome, long token) { }
    }

    internal sealed class BlockingEmptyCodec : IRpcCodec<RpcEmptyRequest>, IDisposable
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new(false);
        public void Serialize(in RpcEmptyRequest value, IBufferWriter<byte> buffer)
        {
            Started.TrySetResult();
            if (!Release.Wait(Timeout))
                throw new TimeoutException("Serialization barrier was not released.");
        }
        public RpcEmptyRequest Deserialize(in ReadOnlySequence<byte> buffer) => default;
        public void Dispose() { Release.Set(); Release.Dispose(); }
    }

    internal sealed class SwitchingTransportFactory(IPEndPoint first, IPEndPoint second) : IClientTransportFactory
    {
        private readonly SocketClientTransportFactory _first = new(first);
        private readonly SocketClientTransportFactory _second = new(second);
        private int _useSecond;
        internal void Switch() => Volatile.Write(ref _useSecond, 1);
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => (Volatile.Read(ref _useSecond) == 0 ? _first : _second).ConnectAsync(cancellationToken);
        public async ValueTask DisposeAsync()
        {
            await _first.DisposeAsync();
            await _second.DisposeAsync();
        }
    }
}
