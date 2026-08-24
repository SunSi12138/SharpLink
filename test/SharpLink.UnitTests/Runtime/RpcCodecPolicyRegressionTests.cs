using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.UnitTests.Runtime;

public sealed class RpcCodecPolicyRegressionTests
{
    [Test]
    public void DirectFactoryWithCustomWireFormatShouldPrepareAndResolve()
    {
        var manifest = new TestManifest(
            typeof(RpcCodecPolicyRegressionTests).Assembly,
            [new DirectPayloadFactory()]);

        using var context = new SharpLinkRuntimeContextBuilder().Build([manifest]);
        var ownerProvider = RpcGeneratedCodecResolver.GetProvider(context, manifest.OwnerAssembly);

        Ensure(ownerProvider.GetCodec<DirectPayload>() is DirectPayloadCodec,
            "a compile-time direct Codec with a custom WireFormatId must survive RuntimeContext preparation and owner resolution");
    }

    [Test]
    public void LateLoadedPolicyOwnerShouldFailClosedInsteadOfUsingGlobalCodecs()
    {
        using var context = new SharpLinkRuntimeContextBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        var owner = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("SharpLink.LatePolicyOwner." + Guid.NewGuid().ToString("N")),
            AssemblyBuilderAccess.RunAndCollect);
        ISharpLinkGeneratedAssemblyManifest? manifest = new TestManifest(
            owner,
            [new DirectPayloadFactory()]);
        SharpLinkGeneratedAssemblyCatalog.Register(manifest);

        try
        {
            _ = RpcGeneratedCodecResolver.GetProvider(context, owner);
            throw new InvalidOperationException(
                "a runtime context that predates a loaded policy owner must not silently return the global Codec provider");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("was not adopted", StringComparison.Ordinal),
                $"late policy owner must fail with the deterministic adoption diagnostic, got: {exception.Message}");
        }
        finally
        {
            manifest = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    [Test]
    public void UnmanagedEnumPolicyShouldOverrideUnsafeBlitFallback()
    {
        using var context = new SharpLinkRuntimeContextBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        var globalEnum = context.Codecs.GetCodec<SampleEnum>();
        var globalNullableEnum = context.Codecs.GetCodec<SampleEnum?>();
        Ensure(globalEnum.GetType().Name.Contains("UnsafeBlitCodec", StringComparison.Ordinal) &&
               globalNullableEnum.GetType().Name.Contains("UnsafeBlitCodec", StringComparison.Ordinal),
            "the regression must exercise the existing unmanaged fallback for enum and nullable enum");

        var adapter = new EnumRouteAdapter();
        var owner = typeof(IRpcRuntimeContext).Assembly;
        var manifest = new TestManifest(
            owner,
            [
                new AdapterFactory<SampleEnum>(adapter),
                new AdapterFactory<SampleEnum?>(adapter)
            ]);
        var registration = context.PrepareGeneratedManifest(manifest);
        context.AdoptGeneratedManifest(registration);

        var ownerProvider = RpcGeneratedCodecResolver.GetProvider(context, owner);
        Ensure(ownerProvider.GetCodec<SampleEnum>() is SampleEnumCodec,
            "an owner-scoped Unmanaged enum route must replace the UnsafeBlit fallback");
        Ensure(ownerProvider.GetCodec<SampleEnum?>() is NullableSampleEnumCodec,
            "an owner-scoped Unmanaged nullable-enum route must replace the UnsafeBlit fallback");
        Ensure(ReferenceEquals(context.Codecs.GetCodec<SampleEnum>(), globalEnum) &&
               ReferenceEquals(context.Codecs.GetCodec<SampleEnum?>(), globalNullableEnum),
            "enum route policy must remain owner-scoped and leave the context-global fallback unchanged");
    }

    private enum SampleEnum : short
    {
        Zero,
        One
    }

    private sealed class DirectPayload
    {
    }

    private sealed class DirectPayloadCodec : IRpcCodec<DirectPayload>
    {
        public void Serialize(in DirectPayload value, IBufferWriter<byte> buffer) { }
        public DirectPayload Deserialize(in ReadOnlySequence<byte> buffer) => new();
    }

    private sealed class DirectPayloadFactory : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(DirectPayload);
        public string SchemaId => "direct-payload-schema/v1";
        public string WireFormatId => "direct-payload-wire/v1";
        public RpcGeneratedCodecFactoryKind Kind => RpcGeneratedCodecFactoryKind.Direct;
        public string? AdapterId => null;
        public IRpcCodecAdapter? Adapter => null;
        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => adapterScope is null
                ? new DirectPayloadCodec()
                : throw new ArgumentException("Direct factory does not accept an adapter scope.", nameof(adapterScope));
        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<DirectPayload>;
    }

    private sealed class EnumRouteAdapter : IRpcCodecAdapter
    {
        public string AdapterId => "enum-unmanaged-route/v1";
        public string WireFormatId => "enum-safe-wire/v1";
        public IRpcCodecAdapterScope CreateScope() => new EnumRouteScope();
    }

    private sealed class EnumRouteScope : IRpcCodecAdapterScope
    {
        public IRpcCodec<T> CreateCodec<T>()
        {
            if (typeof(T) == typeof(SampleEnum))
                return (IRpcCodec<T>)(object)new SampleEnumCodec();
            if (typeof(T) == typeof(SampleEnum?))
                return (IRpcCodec<T>)(object)new NullableSampleEnumCodec();
            throw new NotSupportedException($"Unexpected enum route test target '{typeof(T)}'.");
        }

        public void Dispose() { }
    }

    private sealed class AdapterFactory<T>(EnumRouteAdapter adapter) : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(T);
        public string SchemaId => "enum-route-schema/" + typeof(T).FullName;
        public string WireFormatId => adapter.WireFormatId;
        public string? AdapterId => adapter.AdapterId;
        public IRpcCodecAdapter? Adapter => adapter;
        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => adapterScope?.CreateCodec<T>() ?? throw new ArgumentNullException(nameof(adapterScope));
        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<T>;
    }

    private sealed class SampleEnumCodec : IRpcCodec<SampleEnum>
    {
        public void Serialize(in SampleEnum value, IBufferWriter<byte> buffer) { }
        public SampleEnum Deserialize(in ReadOnlySequence<byte> buffer) => SampleEnum.One;
    }

    private sealed class NullableSampleEnumCodec : IRpcCodec<SampleEnum?>
    {
        public void Serialize(in SampleEnum? value, IBufferWriter<byte> buffer) { }
        public SampleEnum? Deserialize(in ReadOnlySequence<byte> buffer) => SampleEnum.One;
    }

    private sealed class TestManifest(
        Assembly ownerAssembly,
        IReadOnlyList<IRpcGeneratedCodecFactory> contractCodecs) : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "policy-regression";
        public Assembly OwnerAssembly { get; } = ownerAssembly;
        public string CompileTimeDescriptor => "policy-regression|" + OwnerAssembly.FullName;
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs { get; } = contractCodecs;
        public IReadOnlyList<string> Dependencies => [];
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
