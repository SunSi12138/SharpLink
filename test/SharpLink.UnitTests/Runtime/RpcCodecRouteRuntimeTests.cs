using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.StaticCodecOwnerTest.Contracts;
using SharpLink.MultiClusterTest.Contracts;

namespace SharpLink.UnitTests.Runtime;

public class RpcCodecRouteRuntimeTests
{
    [Test]
    public void ContractRoutesShouldCoexistWithoutChangingGlobalBuiltin()
    {
        var routeA = new RoutedInt32Codec("A");
        var ownerA = typeof(IContractA).Assembly;
        var ownerB = typeof(IOrdersContract).Assembly;

        using var context = new SharpLinkRuntimeContextBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        var registrationA = context.PrepareGeneratedManifest(
            new RoutedManifest(ownerA, routeA, "route-a/v1"));
        var registrationB = context.PrepareGeneratedManifest(new DefaultManifest(ownerB));

        Ensure(registrationA.Codecs.Count == 0,
            "Contract-routed targets must not enter the context-global generated Codec registry");
        Ensure(registrationA.ContractCodecs.ContainsKey(typeof(int)),
            "Contract-routed targets must remain available only to their owning Contracts");

        context.AdoptGeneratedManifest(registrationA);
        context.AdoptGeneratedManifest(registrationB);

        var global = context.Codecs.GetCodec<int>();
        var codecsA = RpcGeneratedCodecResolver.GetProvider(context, ownerA);
        var codecsB = RpcGeneratedCodecResolver.GetProvider(context, ownerB);

        Ensure(ReferenceEquals(codecsA.GetCodec<int>(), routeA),
            "owner A must resolve its own routed int Codec");
        Ensure(ReferenceEquals(codecsB.GetCodec<int>(), global),
            "owner B without a Contract Codec binding must keep the context default builtin int Codec");
        Ensure(!ReferenceEquals(global, routeA),
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
        string adapterId) : IRpcCodecAdapter
    {
        public string AdapterId { get; } = adapterId;
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

    private sealed class RoutedInt32Factory(RouteAdapter adapter) : ITestGeneratedCodecFactory
    {
        public Type TargetType => typeof(int);
        public string? AdapterId => adapter.AdapterId;
        public IRpcCodecAdapter? Adapter => adapter;

        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => adapterScope?.CreateCodec<int>() ??
               throw new ArgumentNullException(nameof(adapterScope));

        public bool IsCompatibleCodec(IRpcCodec candidate) => candidate is IRpcCodec<int>;
    }

    private sealed class RoutedManifest : ITestGeneratedManifest
    {
        public RoutedManifest(
            Assembly ownerAssembly,
            RoutedInt32Codec codec,
            string adapterId)
        {
            OwnerAssembly = ownerAssembly;
            ContractCodecs = [new RoutedInt32Factory(new RouteAdapter(codec, adapterId))];
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

    private sealed class DefaultManifest : ITestGeneratedManifest
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
