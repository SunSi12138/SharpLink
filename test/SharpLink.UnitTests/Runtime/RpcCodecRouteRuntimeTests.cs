using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.UnitTests.Runtime;

public class RpcCodecRouteRuntimeTests
{
    [Test]
    public void ContractRoutesShouldCoexistWithoutChangingGlobalBuiltin()
    {
        var routeA = new RoutedInt32Codec("A");
        var routeC = new RoutedInt32Codec("C");
        var ownerA = typeof(RpcCodecRouteRuntimeTests).Assembly;
        var ownerB = typeof(IRpcRuntimeContext).Assembly;
        var ownerC = typeof(SharpLinkRuntimeContext).Assembly;

        using var context = new SharpLinkRuntimeContextBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        var registrationA = context.PrepareGeneratedManifest(
            new RoutedManifest(ownerA, routeA, "route-a/v1", "wire-a/v1"));
        var registrationB = context.PrepareGeneratedManifest(new DefaultManifest(ownerB));
        var registrationC = context.PrepareGeneratedManifest(
            new RoutedManifest(ownerC, routeC, "route-c/v1", "wire-c/v1"));

        Ensure(registrationA.Codecs.Count == 0 && registrationC.Codecs.Count == 0,
            "Contract-routed targets must not enter the context-global generated Codec registry");
        Ensure(registrationA.ContractCodecs.ContainsKey(typeof(int)) &&
               registrationC.ContractCodecs.ContainsKey(typeof(int)),
            "Contract-routed targets must remain available only to their owning Contracts");

        context.AdoptGeneratedManifest(registrationA);
        context.AdoptGeneratedManifest(registrationB);
        context.AdoptGeneratedManifest(registrationC);

        var global = context.Codecs.GetCodec<int>();
        var codecsA = RpcGeneratedCodecResolver.GetProvider(context, ownerA);
        var codecsB = RpcGeneratedCodecResolver.GetProvider(context, ownerB);
        var codecsC = RpcGeneratedCodecResolver.GetProvider(context, ownerC);

        Ensure(ReferenceEquals(codecsA.GetCodec<int>(), routeA),
            "owner A must resolve its own routed int Codec");
        Ensure(ReferenceEquals(codecsB.GetCodec<int>(), global),
            "owner B without a Contract Codec binding must keep the context default builtin int Codec");
        Ensure(ReferenceEquals(codecsC.GetCodec<int>(), routeC),
            "owner C must resolve its own routed int Codec independently of owner A");
        Ensure(!ReferenceEquals(global, routeA) && !ReferenceEquals(global, routeC),
            "Contract routes must never replace the context-global builtin Codec");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class RoutedInt32Codec(string owner) : IRpcCodec<int>
    {
        public string Owner { get; } = owner;

        public void Serialize(in int value, IBufferWriter<byte> buffer)
        {
        }

        public int Deserialize(in ReadOnlySequence<byte> buffer) => 0;
    }

    private sealed class RouteAdapter(
        RoutedInt32Codec codec,
        string adapterId,
        string wireFormatId) : IRpcCodecAdapter
    {
        public string AdapterId { get; } = adapterId;
        public string WireFormatId { get; } = wireFormatId;
        public IRpcCodecAdapterScope CreateScope() => new RouteScope(codec);
    }

    private sealed class RouteScope(RoutedInt32Codec codec) : IRpcCodecAdapterScope
    {
        public IRpcCodec<T> CreateCodec<T>()
            => typeof(T) == typeof(int)
                ? (IRpcCodec<T>)(object)codec
                : throw new NotSupportedException($"Unexpected route test target '{typeof(T)}'.");

        public void Dispose()
        {
        }
    }

    private sealed class RoutedInt32Factory(RouteAdapter adapter) : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(int);
        public string SchemaId => $"route-native-int32-{adapter.AdapterId}";
        public string WireFormatId => adapter.WireFormatId;
        public string? AdapterId => adapter.AdapterId;
        public IRpcCodecAdapter? Adapter => adapter;

        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => adapterScope?.CreateCodec<int>() ??
               throw new ArgumentNullException(nameof(adapterScope));

        public bool IsCompatibleCodec(IRpcCodec candidate) => candidate is IRpcCodec<int>;
    }

    private sealed class RoutedManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public RoutedManifest(
            Assembly ownerAssembly,
            RoutedInt32Codec codec,
            string adapterId,
            string wireFormatId)
        {
            OwnerAssembly = ownerAssembly;
            ContractCodecs = [new RoutedInt32Factory(new RouteAdapter(codec, adapterId, wireFormatId))];
        }

        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "route-test";
        public Assembly OwnerAssembly { get; }
        public string CompileTimeDescriptor => $"route-native-{OwnerAssembly.GetName().Name}";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs { get; }
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class DefaultManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public DefaultManifest(Assembly ownerAssembly)
        {
            OwnerAssembly = ownerAssembly;
            CompileTimeDescriptor = $"route-default-{ownerAssembly.GetName().Name}";
        }

        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "route-test";
        public Assembly OwnerAssembly { get; }
        public string CompileTimeDescriptor { get; }
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }
}
