using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpLink.Client;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Client;

// Source/metadata ownership controls, not physical layout, allocation, inlining or performance proof.
public class SharpLinkContractCallSiteOwnershipTests
{
    private static readonly string[] Families =
    ["InvokeUnaryAsync", "InvokeOneWayAsync", "InvokeClientStreamingAsync", "InvokeServerStreamingAsync", "InvokeDuplexStreamingAsync"];

    [Test]
    public void IdentityNeutralFacadeIsAbsentAndAllFiveSiteAdaptersRemainSynchronous()
    {
        Require(!typeof(IRpcContractChannel).IsAssignableFrom(typeof(SharpLinkClient)),
            "The root client must not expose an identity-neutral invocation channel.");
        Require(typeof(IRpcContractChannel).Assembly.GetType("SharpLink.Abstractions.IRpcChannel") is null,
            "The removed identity-neutral interface must not remain as a compatibility alias.");
        Require(!typeof(SharpLinkClient).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Any(method => Families.Contains(method.Name, StringComparer.Ordinal)),
            "Raw public invocation forwarding methods must not survive on the root client.");
        CheckSlots(typeof(SharpLinkClient.ContractCallSite));
    }

    [Test]
    public void FinitePreparationAndCombinedAbiMustHaveOneExactPublicShape()
    {
        Require(SharpLinkGeneratedManifestVersions.Api == 5 && SharpLinkGeneratedManifestVersions.Protocol == 3 &&
            SharpLinkGeneratedManifestVersions.AbiIdentity == "sharplink-3.0-api5-contract-channel-complete-method-facts-v1",
            "Current generation must require the single combined channel/complete-facts ABI.");
        foreach (var owner in new[] { typeof(IRpcContractChannel), typeof(RpcProxy<IContractA>) })
        {
            var prepare = owner.GetMethods().Where(method => method.Name == "PrepareAsync").ToArray();
            Require(prepare.Length == 1 && prepare[0].ReturnType == typeof(ValueTask),
                "Preparation must have one finite ValueTask API, with no timeoutless fallback.");
            var parameters = prepare[0].GetParameters();
            Require(parameters.Length == 2 && parameters[0].ParameterType == typeof(TimeSpan) &&
                !parameters[0].HasDefaultValue && parameters[1].ParameterType == typeof(CancellationToken) &&
                parameters[1].HasDefaultValue, "Prepare must require timeout and retain optional caller cancellation.");
        }
        foreach (var owner in new[] { typeof(ISharpLinkClient), typeof(ISharpLinkMultiClusterClient) })
            Require(!owner.GetMethods().Any(method => method.Name is "Get" or "GetWithMetadata"),
                "Old generated factory aliases must not remain beside the new channel API.");
    }

    [Test]
    public void InvocationStateMachinesAreOwnedByTheSiteNotAnOuterAsyncBridge()
    {
        var site = typeof(SharpLinkClient.ContractCallSite);
        var fields = site.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        Require(fields.Count(f => f.FieldType == typeof(SharpLinkClient) && f.IsInitOnly) == 1 &&
            !fields.Any(f => f.FieldType == typeof(Type) || f.FieldType == typeof(Assembly) ||
                typeof(IRpcCodec).IsAssignableFrom(f.FieldType) || typeof(IRpcCodecProvider).IsAssignableFrom(f.FieldType) ||
                f.FieldType == typeof(SharpLinkDynamicModule)),
            "Site must retain one explicit client owner and no collectible contract/module/codec payload.");
        string[] required = ["AwaitLogicalInvocationAsync", "AwaitPlainOneWayEmissionOrDeadlineAsync", "InvokeOneWayPlainCoreAsync",
            "InvokeOneWayStreamingCoreAsync", "InvokeClientStreamingCoreAsync", "RunGeneratedClientStreamsAsync",
            "StartServerStreamingInvokerAsync", "StartDuplexStreamingInvokerAsync", "InvokeUnaryWithRetryAsync", "DelayForRetryOrAdmissionAsync"];
        foreach (var name in required)
        {
            var methods = site.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Where(m => m.Name == name).ToArray();
            Require(methods.Length > 0, "Missing moved async owner: " + name);
            foreach (var method in methods)
            {
                var state = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
                Require(state is not null && state.DeclaringType == site, "Missing site-owned state machine: " + name);
                Require(!state!.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Any(f => f.FieldType == typeof(SharpLinkClient)), "Unexpected second direct client capture: " + name);
            }
            Require(!typeof(SharpLinkClient).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Any(m => m.Name == name), "Moved async helper was copied rather than extracted: " + name);
        }
    }

    [Test]
    public async Task RegistrationAndMetadataFactoriesUseOneSitePerRegistrationNotTheRawFacade()
    {
        var builder = SharpClientBuilder.Create().DisableRequestTimeout().UseTransport(new TestClientTransportFactory());
        var plan = builder.CompileForMultiCluster([new Manifest()]);
        await using var client = (SharpLinkClient)builder.MaterializeCompiledPlan(plan);
        var first = (ProxyA)client.CreateProxy<IContractA>().Service;
        var repeated = (ProxyA)client.CreateProxy<IContractA>().Service;
        var second = (ProxyB)client.CreateProxy<IContractB>().Service;
        Require(ReferenceEquals(first.Channel, repeated.Channel), "Repeated cold factory handles must retain the same full-key site.");
        var firstSite = first.Channel as SharpLinkClient.ContractCallSite;
        var secondSite = second.Channel as SharpLinkClient.ContractCallSite;
        Require(firstSite is not null && firstSite.ContractId == 92001 && secondSite is not null && secondSite.ContractId == 92002,
            "Registration factories must receive their direct scalar-identity sites.");
        Require(!ReferenceEquals(firstSite, secondSite), "Different registrations must not share a client-global site.");
        var metadata = new SharpLinkMetadata(new KeyValuePair<string, string>("e0-test", "value"));
        var metadataProxy = (ProxyA)client.CreateProxy<IContractA>(new RpcProxyOptions { Metadata = metadata }).Service;
        Require(metadataProxy.Channel is SharpLinkMetadataRpcChannel, "Metadata wrapper was removed.");
        Require(ReferenceEquals(InnerChannel(metadataProxy.Channel), firstSite), "Metadata route must wrap the same registration site.");
        var emptyMetadata = (ProxyA)client.CreateProxy<IContractA>(new RpcProxyOptions { Metadata = new SharpLinkMetadata() }).Service;
        Require(ReferenceEquals(emptyMetadata.Channel, firstSite), "Empty metadata must not install a wrapper or a second site.");
        Require(ReferenceEquals(client.RuntimeContext, firstSite!.RuntimeContext), "Direct public runtime context capability changed.");
        Require(typeof(SharpLinkClient).GetField("_rawCallSite", BindingFlags.Instance | BindingFlags.NonPublic) is null,
            "The identity-neutral fallback site must be absent.");
    }

    [Test]
    public async Task ActualGeneratedProxyFactoryStoresTheRegistrationSiteDirectly()
    {
        var contract = typeof(SharpLink.StaticCodecOwnerTest.Contracts.IGeneratedRawOwnerContract);
        var loaded = SharpLinkAssemblyManifestLoader.TryLoad(contract.Assembly, out var manifest);
        Require(loaded.Succeeded && manifest is not null, "Actual generated manifest was not loaded.");
        var builder = SharpClientBuilder.Create().DisableRequestTimeout().UseTransport(new TestClientTransportFactory());
        var plan = builder.CompileForMultiCluster([manifest!]);
        await using var client = (SharpLinkClient)builder.MaterializeCompiledPlan(plan);
        var proxy = client.CreateProxy<SharpLink.StaticCodecOwnerTest.Contracts.IGeneratedRawOwnerContract>().Service;
        var site = InnerChannel(proxy) as SharpLinkClient.ContractCallSite;
        var descriptor = manifest!.Contracts.Single(d => d.ContractType == contract);
        Require(site is not null && site.ContractId == descriptor.ContractId, "Real generated proxy stores an outer client/wrong site.");
        var metadataProxy = client.CreateProxy<SharpLink.StaticCodecOwnerTest.Contracts.IGeneratedRawOwnerContract>(new RpcProxyOptions
        {
            Metadata = new SharpLinkMetadata(new KeyValuePair<string, string>("e0-test", "value"))
        }).Service;
        var metadataChannel = InnerChannel(metadataProxy);
        Require(metadataChannel is SharpLinkMetadataRpcChannel && ReferenceEquals(InnerChannel(metadataChannel), site),
            "Actual generated metadata proxy must wrap the existing registration site.");
    }

    [Test]
    public async Task ModuleAndMetadataWrappersRetainTheExplicitSiteAndRuntimeContext()
    {
        var builder = SharpClientBuilder.Create().DisableRequestTimeout().UseTransport(new TestClientTransportFactory());
        await using var client = (SharpLinkClient)builder.MaterializeCompiledPlan(builder.CompileForMultiCluster([new Manifest()]));
        using var runtime = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var manifest = new Manifest();
        using var registration = runtime.PrepareGeneratedManifest(manifest);
        var module = new SharpLinkDynamicModule(typeof(Manifest).Assembly, manifest, registration);
        var site = (SharpLinkClient.ContractCallSite)client.CreateContractChannel(client.GetLocalContract<IContractA>());
        var moduleChannel = new SharpLinkModuleRpcChannel(site, module);
        var metadataChannel = new SharpLinkMetadataRpcChannel(moduleChannel,
            new SharpLinkMetadata(new KeyValuePair<string, string>("e0-test", "value")));
        Require(ReferenceEquals(InnerChannel(moduleChannel), site) && ReferenceEquals(InnerChannel(metadataChannel), moduleChannel),
            "Module/metadata wrapper order or inner owner changed.");
        Require(ReferenceEquals(metadataChannel.RuntimeContext, client.RuntimeContext), "Wrapper runtime context changed.");
    }

    [Test]
    public async Task ScopedArbitraryDescriptorsKeepNullCodecBeforeCancellationOrderingAcrossAllFiveFamilies()
    {
        var builder = SharpClientBuilder.Create().DisableRequestTimeout().UseTransport(new TestClientTransportFactory());
        await using var client = (SharpLinkClient)builder.MaterializeCompiledPlan(builder.CompileForMultiCluster([new Manifest()]));
        var local = client.GetLocalContract<IContractA>();
        IRpcContractChannel[] channels =
        [
            client.CreateContractChannel(local),
            client.CreateContractChannel(local, new RpcProxyOptions
            {
                Metadata = new SharpLinkMetadata(new KeyValuePair<string, string>("owner", "metadata"))
            })
        ];
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var request = default(RpcEmptyRequest); var streams = default(RpcNoClientStreams);
        foreach (var channel in channels)
        {
            // The descriptor deliberately differs from this exact registered channel; null codec still wins first.
            var method = new RpcMethodDescriptor(987654321, 1, RpcMethodKind.Unary, true, false, false, null);
            var response = channel.RuntimeContext.Codecs.GetCodec<int>();
            NullRequest(() => { _ = channel.InvokeUnaryAsync(method, request, null!, response, null, cancellation.Token); });
            NullRequest(() => { _ = channel.InvokeOneWayAsync<RpcEmptyRequest, RpcNoClientStreams>(method, request, null!, streams, null, cancellation.Token); });
            NullRequest(() => { _ = channel.InvokeClientStreamingAsync(method, request, null!, response, streams, null, cancellation.Token); });
            NullRequest(() => { _ = channel.InvokeServerStreamingAsync(method, request, null!, response, null, cancellation.Token); });
            NullRequest(() => { _ = channel.InvokeDuplexStreamingAsync(method, request, null!, response, streams, null, cancellation.Token); });
        }
    }

    private static void CheckSlots(Type owner)
    {
        var map = owner.GetInterfaceMap(typeof(IRpcContractChannel));
        var slots = map.InterfaceMethods.Select((method, index) => (method, target: map.TargetMethods[index]))
            .Where(x => Families.Contains(x.method.Name, StringComparer.Ordinal)).ToArray();
        Require(slots.Length == 5 && slots.Select(x => x.method.Name).Distinct().Count() == 5, "Exact five public interface slots missing.");
        foreach (var slot in slots)
        {
            Require(slot.target.DeclaringType == owner && !slot.target.IsDefined(typeof(AsyncStateMachineAttribute)),
                "Mapped compiler adapter is not a synchronous declared owner: " + slot.target);
            var methods = owner.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == slot.method.Name && Signature(m) == Signature(slot.method)).ToArray();
            Require(methods.Length == 1 && !methods[0].IsDefined(typeof(AsyncStateMachineAttribute)),
                "Unique exact public nonasync facade missing behind mapped adapter: " + slot.method.Name);
        }
    }
    private static string Signature(MethodInfo method) => method.GetGenericArguments().Length + ":" + TypeKey(method.ReturnType) + "(" +
        string.Join(",", method.GetParameters().Select(p => TypeKey(p.ParameterType) + (p.IsIn ? ":in" : "") + (p.IsOut ? ":out" : ""))) + ")";
    private static string TypeKey(Type type) => type.IsGenericParameter ? "!" + type.GenericParameterPosition :
        type.IsByRef ? TypeKey(type.GetElementType()!) + "&" : type.IsArray ? TypeKey(type.GetElementType()!) + "[]" :
        type.IsGenericType ? type.GetGenericTypeDefinition().FullName + "<" + string.Join(",", type.GetGenericArguments().Select(TypeKey)) + ">" : type.FullName!;
    private static IRpcContractChannel InnerChannel(object wrapper) => (IRpcContractChannel)wrapper.GetType()
        .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .Single(f => typeof(IRpcContractChannel).IsAssignableFrom(f.FieldType)).GetValue(wrapper)!;
    private static void NullRequest(Action action)
    {
        try { action(); } catch (ArgumentNullException exception) when (exception.ParamName == "requestCodec") { return; }
        throw new InvalidOperationException("Expected synchronous requestCodec validation before cancellation/registration lookup.");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private interface IContractA : IService;
    private interface IContractB : IService;
    private sealed class ProxyA(IRpcContractChannel channel) : IContractA { internal IRpcContractChannel Channel { get; } = channel; }
    private sealed class ProxyB(IRpcContractChannel channel) : IContractB { internal IRpcContractChannel Channel { get; } = channel; }
    private sealed class Manifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "e0-recovery-test";
        public Assembly OwnerAssembly => typeof(Manifest).Assembly;
        public RpcHash128 RpcAssemblyHash => new(0x65302d7265636f76UL, 0x6572792d74657374UL);
        public string CompileTimeDescriptor => "e0-recovery-test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; } =
        [
            new(typeof(IContractA), typeof(IContractA).FullName!, 92001, new string('a', 64), [],
                static (channel, _) => new ProxyA(channel), static _ => throw new NotSupportedException()),
            new(typeof(IContractB), typeof(IContractB).FullName!, 92002, new string('b', 64), [],
                static (channel, _) => new ProxyB(channel), static _ => throw new NotSupportedException())
        ];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }
}
