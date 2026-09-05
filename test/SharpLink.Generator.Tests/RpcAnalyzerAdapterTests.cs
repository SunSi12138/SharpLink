using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{

    [Test]
    public Task RegisteredSelectorShouldGenerateClosedAdapterFactoryWithoutReflection()
    {
        var source = AddAssemblyAttribute(BuildSource("""
[FakePackable]
public sealed class Graph
{
    public Graph? Parent { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IGraphService : SharpLink.Sdk.IService
{
    ValueTask<Graph> Echo(Graph value);
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class FakePackableAttribute : Attribute { }

public sealed class FakeAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "fake.adapter/v1";
    public string WireFormatId => "fake-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FakeAdapter), \"fake.adapter/v1\", \"fake-wire/v1\", SelectorAttributeType = typeof(FakePackableAttribute))]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("adapterScope.CreateCodec<global::Graph>()", StringComparison.Ordinal),
            "Adapter factory must emit a closed generic Codec creation");
        Ensure(generated.Contains("public Type TargetType => typeof(global::Graph);", StringComparison.Ordinal),
            "Adapter factory target type");
        Ensure(generated.Contains("fake.adapter/v1", StringComparison.Ordinal), "Adapter ID");
        Ensure(generated.Contains("public RpcHash128 CodecHash => new(", StringComparison.Ordinal),
            "Adapter factory CodecHash");
        Ensure(!generated.Contains("SchemaId =>", StringComparison.Ordinal) &&
               !generated.Contains("WireFormatId =>", StringComparison.Ordinal),
            "Adapter factory must not emit legacy schema/wire identities");
        Ensure(!generated.Contains("FakeAdapter, Version=", StringComparison.Ordinal),
            "Adapter implementation assemblies are normal runtime references, not dynamic Manifest dependencies");
        Ensure(!generated.Contains("MakeGenericType", StringComparison.Ordinal), "no MakeGenericType");
        Ensure(!generated.Contains("Activator.CreateInstance", StringComparison.Ordinal), "no Activator");
        Ensure(!generated.Contains("Serialize(Type", StringComparison.Ordinal), "no non-generic Serialize API");
        Ensure(!generated.Contains("Deserialize(Type", StringComparison.Ordinal), "no non-generic Deserialize API");
        EnsureDoesNotHaveRule(source, "SHARPLINK010");
        return Task.CompletedTask;
    }

    [Test]
    public Task ExplicitAdapterBindingShouldSelectRegisteredAdapter()
    {
        var source = AddAssemblyAttribute(BuildSource("""
[SharpLink.Sdk.RpcCodecAdapter(typeof(FakeAdapter))]
public sealed class Graph
{
    public Graph? Parent { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IGraphService : SharpLink.Sdk.IService
{
    ValueTask<Graph> Echo(Graph value);
}

public sealed class FakeAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "fake.adapter/v1";
    public string WireFormatId => "fake-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FakeAdapter), \"fake.adapter/v1\", \"fake-wire/v1\")]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("CreateCodec<global::Graph>()", StringComparison.Ordinal),
            "explicit binding generates Adapter factory");
        EnsureDoesNotHaveRule(source, "SHARPLINK010");
        return Task.CompletedTask;
    }

    [Test]
    public Task InvalidAdapterRegistrationShouldReportSharplink043()
    {
        var source = AddAssemblyAttribute(BuildSource("""
public sealed class InvalidAdapter { }
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(InvalidAdapter), \"invalid/v1\", \"wire/v1\")]");
        EnsureHasRule(source, "SHARPLINK043");
        return Task.CompletedTask;
    }

    [Test]
    public Task InvalidAdapterRegistrationShapesShouldReportSharplink042()
    {
        var declarations = """
public sealed class ValidAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "valid.adapter/v1";
    public string WireFormatId => "valid-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}

public sealed class NotAnAttribute { }
""";
        var invalidAttributes = new[]
        {
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(ValidAdapter), \"\", \"wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(ValidAdapter), \"adapter/v1\", \"\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(ValidAdapter), \"adapter/v1\", \"wire/é\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(ValidAdapter), \"adapter/v1\", \"wire/v1\", SelectorAttributeType = typeof(NotAnAttribute))]"
        };

        foreach (var attribute in invalidAttributes)
            EnsureHasRule(AddAssemblyAttribute(BuildSource(declarations), attribute), "SHARPLINK042");
        return Task.CompletedTask;
    }

    [Test]
    public Task InvalidAdapterTypeShapesShouldReportSharplink043()
    {
        var source = AddAssemblyAttributes(BuildSource("""
public class NonSealedAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "nonsealed/v1";
    public string WireFormatId => "wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}

internal sealed class NonPublicAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "nonpublic/v1";
    public string WireFormatId => "wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}

public sealed class NoPublicConstructorAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    private NoPublicConstructorAdapter() { }
    public string AdapterId => "no-ctor/v1";
    public string WireFormatId => "wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}

public sealed class DoesNotImplementAdapter { }
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(NonSealedAdapter), \"nonsealed/v1\", \"wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(NonPublicAdapter), \"nonpublic/v1\", \"wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(NoPublicConstructorAdapter), \"no-ctor/v1\", \"wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(DoesNotImplementAdapter), \"no-interface/v1\", \"wire/v1\")]");

        EnsureRuleCount(source, "SHARPLINK043", 4);
        return Task.CompletedTask;
    }

    [Test]
    public Task AdapterNestedInNonPublicTypeShouldReportSharplink043()
    {
        var source = AddAssemblyAttribute(BuildSource("""
internal static class HiddenContainer
{
    public sealed class NestedAdapter : SharpLink.Abstractions.IRpcCodecAdapter
    {
        public string AdapterId => "nested/v1";
        public string WireFormatId => "wire/v1";
        public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
    }
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(HiddenContainer.NestedAdapter), \"nested/v1\", \"wire/v1\")]");

        EnsureHasRule(source, "SHARPLINK043");
        return Task.CompletedTask;
    }

    [Test]
    public Task ConflictingAdapterSelectionShouldReportSharplink045()
    {
        var source = AddAssemblyAttribute(AddAssemblyAttribute(BuildSource("""
[FirstSelector]
[SharpLink.Sdk.RpcCodecAdapter(typeof(SecondAdapter))]
public sealed class Graph { }

[SharpLink.Sdk.RpcContract]
public interface IGraphService : SharpLink.Sdk.IService
{
    ValueTask<Graph> Echo(Graph value);
}

[AttributeUsage(AttributeTargets.Class)] public sealed class FirstSelectorAttribute : Attribute { }
public sealed class FirstAdapter : TestAdapterBase { }
public sealed class SecondAdapter : TestAdapterBase { }
public abstract class TestAdapterBase : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => GetType().Name;
    public string WireFormatId => GetType().Name;
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FirstAdapter), \"first/v1\", \"first-wire/v1\", SelectorAttributeType = typeof(FirstSelectorAttribute))]"),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(SecondAdapter), \"second/v1\", \"second-wire/v1\")]");
        EnsureHasRule(source, "SHARPLINK045");
        return Task.CompletedTask;
    }

    [Test]
    public Task InvalidAdapterAttributeFormsShouldReportSharplink046()
    {
        var source = AddAssemblyAttribute(BuildSource("""
[SharpLink.Sdk.RpcCodecAdapter(typeof(Graph), typeof(FakeAdapter))]
public sealed class Graph { }

[SharpLink.Sdk.RpcContract]
public interface IGraphService : SharpLink.Sdk.IService
{
    ValueTask<Graph> Echo(Graph value);
}

public sealed class FakeAdapter { }
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(FakeAdapter))]");

        EnsureRuleCount(source, "SHARPLINK046", 2);
        return Task.CompletedTask;
    }

    [Test]
    public Task AdapterIdentityConflictsShouldReportSharplink048()
    {
        var sameTypeDifferentIdentity = AddAssemblyAttributes(BuildSource("""
public sealed class FirstAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "first/v1";
    public string WireFormatId => "wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FirstAdapter), \"first/v1\", \"wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FirstAdapter), \"second/v1\", \"wire/v1\")]");
        EnsureHasRuleContaining(sameTypeDifferentIdentity, "SHARPLINK048", "same Adapter type");

        var sameIdDifferentType = AddAssemblyAttributes(BuildSource("""
public sealed class FirstAdapter : AdapterBase { }
public sealed class SecondAdapter : AdapterBase { }
public abstract class AdapterBase : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "shared/v1";
    public string WireFormatId => "wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FirstAdapter), \"shared/v1\", \"wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(SecondAdapter), \"shared/v1\", \"wire/v1\")]");
        EnsureHasRuleContaining(sameIdDifferentType, "SHARPLINK048", "Adapter ID 'shared/v1'");
        return Task.CompletedTask;
    }

    [Test]
    public Task BuiltinAdapterBindingShouldReportSharplink049()
    {
        var source = AddAssemblyAttribute(BuildSource("public sealed class FakeAdapter { }"),
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(int), typeof(FakeAdapter))]");

        EnsureHasRule(source, "SHARPLINK049");
        return Task.CompletedTask;
    }

    [Test]
    public Task UnregisteredSelectedAdapterShouldReportSharplink042()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcCodecAdapter(typeof(FakeAdapter))]
public sealed class Graph
{
    public Graph? Parent { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IGraphService : SharpLink.Sdk.IService
{
    ValueTask<Graph> Echo(Graph value);
}

public sealed class FakeAdapter { }
""");

        EnsureHasRuleContaining(source, "SHARPLINK042", "no valid RpcCodecAdapterRegistration");
        return Task.CompletedTask;
    }

    [Test]
    public Task EquivalentAdapterCandidatesShouldBeIdempotent()
    {
        var source = AddAssemblyAttributes(BuildSource("""
[FakePackable]
[SharpLink.Sdk.RpcCodecAdapter(typeof(FakeAdapter))]
public sealed class Graph
{
    public Graph? Parent { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IGraphService : SharpLink.Sdk.IService
{
    ValueTask<Graph> Echo(Graph value);
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class FakePackableAttribute : Attribute { }

public sealed class FakeAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "fake.adapter/v1";
    public string WireFormatId => "fake-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FakeAdapter), \"fake.adapter/v1\", \"fake-wire/v1\", SelectorAttributeType = typeof(FakePackableAttribute))]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(Graph), typeof(FakeAdapter))]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        EnsureDoesNotHaveRule(source, "SHARPLINK045");
        Ensure(CountOccurrences(generated, "CreateCodec<global::Graph>()") == 1,
            "equivalent type, assembly, and selector candidates emit one factory");
        return Task.CompletedTask;
    }

    [Test]
    public Task RegisteredAdapterShouldNotReplaceSupportedNativeDto()
    {
        var source = AddAssemblyAttribute(BuildSource("""
public sealed class NativePayload
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

[SharpLink.Sdk.RpcContract]
public interface INativeService : SharpLink.Sdk.IService
{
    ValueTask<NativePayload> Echo(NativePayload value);
}

public sealed class InstalledAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "installed/v1";
    public string WireFormatId => "installed-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(InstalledAdapter), \"installed/v1\", \"installed-wire/v1\")]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("IRpcCodec<global::NativePayload>", StringComparison.Ordinal),
            "supported DTO retains its native generated Codec");
        Ensure(generated.Contains("public RpcHash128 CodecHash => new(", StringComparison.Ordinal),
            "supported DTO publishes deterministic native CodecHash");
        Ensure(!generated.Contains("CreateCodec<global::NativePayload>()", StringComparison.Ordinal),
            "installed Adapter is not an automatic fallback");
        Ensure(!generated.Contains("installed-wire/v1", StringComparison.Ordinal),
            "unused Adapter metadata is not emitted");
        return Task.CompletedTask;
    }

    [Test]
    public Task TransitiveAdapterRegistrationShouldBeDiscoveredFromMetadata()
    {
        var sdk = CreateMetadataReference("AdapterMetadataSdk", BuildSource(string.Empty));
        var adapter = CreateAdapterPackageReference(
            "MetadataAdapterPackage",
            "MetadataAdapterPackage",
            "MetadataAdapter",
            "MetadataSelectorAttribute",
            "metadata.adapter/v1",
            "metadata-wire/v1",
            sdk);
        var bridge = CreateMetadataReference(
            "MetadataAdapterBridge",
            "namespace MetadataAdapterBridge { public sealed class Marker { public MetadataAdapterPackage.MetadataAdapter Adapter { get; } = new(); } }",
            sdk,
            adapter);
        var source = """
using System.Threading.Tasks;
using MetadataAdapterPackage;

[MetadataSelector]
public sealed class Graph
{
    public Graph? Parent { get; set; }
}

public sealed class CompileReference
{
    public MetadataAdapterBridge.Marker? Marker { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IGraphService : SharpLink.Sdk.IService
{
    ValueTask<Graph> Echo(Graph value);
}
""";

        var generated = string.Join("\n", RunGeneratorAndGetSources(source, sdk, bridge, adapter));
        Ensure(generated.Contains("CreateCodec<global::Graph>()", StringComparison.Ordinal),
            "registration from the transitive compilation reference closure selects the Adapter");
        Ensure(generated.Contains("metadata.adapter/v1", StringComparison.Ordinal), "metadata Adapter ID");
        Ensure(generated.Contains("public RpcHash128 CodecHash => new(", StringComparison.Ordinal),
            "metadata Adapter CodecHash");
        Ensure(!generated.Contains("metadata-wire/v1", StringComparison.Ordinal),
            "legacy metadata wire identity must not be emitted");
        return Task.CompletedTask;
    }

    [Test]
    public Task AdapterOutputShouldBeDeterministicAcrossReferenceAndAttributeOrder()
    {
        var sdk = CreateMetadataReference("DeterministicAdapterSdk", BuildSource(string.Empty));
        var firstAdapter = CreateAdapterPackageReference(
            "FirstAdapterPackage", "FirstAdapterPackage", "FirstAdapter", "FirstSelectorAttribute",
            "first.adapter/v1", "first-wire/v1", sdk);
        var secondAdapter = CreateAdapterPackageReference(
            "SecondAdapterPackage", "SecondAdapterPackage", "SecondAdapter", "SecondSelectorAttribute",
            "second.adapter/v1", "second-wire/v1", sdk);
        const string body = """
[FirstSelector]
public sealed class FirstGraph { public FirstGraph? Parent { get; set; } }

[SecondSelector]
public sealed class SecondGraph { public SecondGraph? Parent { get; set; } }

[SharpLink.Sdk.RpcContract]
public interface IGraphService : SharpLink.Sdk.IService
{
    ValueTask<FirstGraph> EchoFirst(FirstGraph value);
    ValueTask<SecondGraph> EchoSecond(SecondGraph value);
}
""";
        var firstSource = $$"""
using System.Threading.Tasks;
using FirstAdapterPackage;
using SecondAdapterPackage;
[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(FirstGraph), typeof(FirstAdapterPackage.FirstAdapter))]
[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(SecondGraph), typeof(SecondAdapterPackage.SecondAdapter))]
{{body}}
""";
        var secondSource = $$"""
using System.Threading.Tasks;
using FirstAdapterPackage;
using SecondAdapterPackage;
[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(SecondGraph), typeof(SecondAdapterPackage.SecondAdapter))]
[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(FirstGraph), typeof(FirstAdapterPackage.FirstAdapter))]
{{body}}
""";

        var first = RunGeneratorAndGetSources(firstSource, sdk, firstAdapter, secondAdapter);
        var second = RunGeneratorAndGetSources(secondSource, secondAdapter, firstAdapter, sdk);

        Ensure(first.SequenceEqual(second, StringComparer.Ordinal),
            "reference and equivalent Attribute ordering must not change generated output");
        return Task.CompletedTask;
    }

    [Test]
    public Task MultipleTargetsShouldShareOneGeneratedAdapterHolder()
    {
        var source = AddAssemblyAttribute(BuildSource("""
[FakePackable]
public sealed class FirstGraph { public FirstGraph? Parent { get; set; } }

[FakePackable]
public sealed class SecondGraph { public SecondGraph? Parent { get; set; } }

[SharpLink.Sdk.RpcContract]
public interface IGraphService : SharpLink.Sdk.IService
{
    ValueTask<FirstGraph> EchoFirst(FirstGraph value);
    ValueTask<SecondGraph> EchoSecond(SecondGraph value);
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class FakePackableAttribute : Attribute { }

public sealed class FakeAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "fake.adapter/v1";
    public string WireFormatId => "fake-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FakeAdapter), \"fake.adapter/v1\", \"fake-wire/v1\", SelectorAttributeType = typeof(FakePackableAttribute))]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(CountOccurrences(generated, "new global::FakeAdapter();") == 1,
            "one Manifest emits one Adapter holder for all targets sharing an Adapter ID");
        Ensure(CountOccurrences(generated, "CreateCodec<global::FirstGraph>()") == 1, "first closed target");
        Ensure(CountOccurrences(generated, "CreateCodec<global::SecondGraph>()") == 1, "second closed target");
        return Task.CompletedTask;
    }

    [Test]
    public Task CustomRpcCodecShouldEmitAStableGeneratedFactory()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcCodec(typeof(MoneyCodec))]
public sealed record Money(decimal Value);

[SharpLink.Sdk.RpcCodecImplementation("money-wire/v1", "money-schema/v1")]
public sealed class MoneyCodec : SharpLink.Abstractions.IRpcCodec<Money>
{
}

[SharpLink.Sdk.RpcContract]
public interface IMoneyService : SharpLink.Sdk.IService
{
    ValueTask<Money> Convert(Money value, CancellationToken cancellationToken);
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("internal sealed class Factory : IRpcGeneratedCodecFactory", StringComparison.Ordinal),
            "custom Codec binding must emit an IRpcGeneratedCodecFactory");
        Ensure(generated.Contains("new global::MoneyCodec()", StringComparison.Ordinal),
            "custom Codec factory must construct the bound implementation directly");
        Ensure(generated.Contains("public RpcHash128 CodecHash => new(", StringComparison.Ordinal),
            "custom Codec factory must emit deterministic CodecHash");
        Ensure(!generated.Contains("SchemaId =>", StringComparison.Ordinal) &&
               !generated.Contains("WireFormatId =>", StringComparison.Ordinal),
            "custom Codec factory must not emit legacy schema/wire identities");
        return Task.CompletedTask;
    }

    [Test]
    public Task CustomRpcCodecWithoutStableIdentityShouldReportSharplink061()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcCodec(typeof(MoneyCodec))]
public sealed record Money(decimal Value);

public sealed class MoneyCodec : SharpLink.Abstractions.IRpcCodec<Money>
{
}

[SharpLink.Sdk.RpcContract]
public interface IMoneyService : SharpLink.Sdk.IService
{
    ValueTask<Money> Convert(Money value, CancellationToken cancellationToken);
}
""");

        EnsureRuleCount(source, "SHARPLINK061", 1);
        return Task.CompletedTask;
    }

    [Test]
    public Task NamedTupleAssemblyBindingShouldSelectRegisteredAdapter()
    {
        var source = AddAssemblyAttribute(AddAssemblyAttribute(BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface ITupleService : SharpLink.Sdk.IService
{
    ValueTask<(int Index, string Label)> Echo((int Index, string Label) value);
}

public sealed class FakeAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "fake.adapter/v1";
    public string WireFormatId => "fake-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FakeAdapter), \"fake.adapter/v1\", \"fake-wire/v1\")]"),
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(ValueTuple<int, string>), typeof(FakeAdapter))]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("CreateCodec<global::System.ValueTuple", StringComparison.Ordinal),
            "named tuple resolves through one canonical underlying ValueTuple Codec identity");
        Ensure(!generated.Contains("CreateCodec<(int Index, string Label)>()", StringComparison.Ordinal),
            "tuple element names must not participate in the Codec graph identity");
        EnsureDoesNotHaveRule(source, "SHARPLINK009");
        return Task.CompletedTask;
    }

    [Test]
    public Task AssemblyLevelCustomRpcCodecShouldBindExternalType()
    {
        var source = AddAssemblyAttribute(BuildSource("""
public sealed record ThirdPartyMoney(decimal Value);

[SharpLink.Sdk.RpcCodecImplementation("third-party/v1", "third-party-schema/v1")]
public sealed class ThirdPartyMoneyCodec : SharpLink.Abstractions.IRpcCodec<ThirdPartyMoney>
{
}

[SharpLink.Sdk.RpcContract]
public interface IThirdPartyMoneyService : SharpLink.Sdk.IService
{
    ValueTask<ThirdPartyMoney> Convert(ThirdPartyMoney value, CancellationToken cancellationToken);
}
"""), "[assembly: SharpLink.Sdk.RpcCodec(typeof(ThirdPartyMoney), typeof(ThirdPartyMoneyCodec))]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("new global::ThirdPartyMoneyCodec()", StringComparison.Ordinal),
            "assembly-level custom Codec binding must be used for the external payload type");
        return Task.CompletedTask;
    }

    [Test]
    public Task ReferencedContractAssemblyCustomCodecBindingShouldBeDiscovered()
    {
        var sdk = CreateMetadataReference("SharpLink.Sdk", BuildSource(string.Empty));
        var external = CreateMetadataReference(
            "ExternalCustomCodec",
            """
using SharpLink.Abstractions;
using SharpLink.Sdk;

[assembly: RpcCodec(typeof(ExternalMoney), typeof(ExternalMoneyCodec))]

public sealed record ExternalMoney(decimal Value);

[RpcCodecImplementation("external-wire/v1", "external-schema/v1")]
public sealed class ExternalMoneyCodec : IRpcCodec<ExternalMoney>
{
}
""",
            sdk);
        var source = """
using System.Threading;
using System.Threading.Tasks;
using ExternalCustomCodec;
using SharpLink.Sdk;

[RpcContract]
public interface IExternalMoneyService : IService
{
    ValueTask<ExternalMoney> Convert(ExternalMoney value, CancellationToken cancellationToken);
}
""";

        var generated = string.Join("\n", RunGeneratorAndGetSources(source, sdk, external));
        Ensure(!generated.Contains("new global::ExternalMoneyCodec()", StringComparison.Ordinal),
            "assembly-level custom Codec policy from a referenced assembly must not leak into the current Contract owner");
        Ensure(!generated.Contains("\"external-wire/v1\"", StringComparison.Ordinal),
            "referenced assembly-level custom Codec wire identity must not be inherited by the current owner");
        return Task.CompletedTask;
    }

    [Test]
    public Task SelectorShouldOverrideUnmanagedNativeFallback()
    {
        var source = AddAssemblyAttribute(BuildSource("""
[FakePackable]
public readonly struct Point
{
    public int X { get; init; }
    public int Y { get; init; }
}

[SharpLink.Sdk.RpcContract]
public interface IPointService : SharpLink.Sdk.IService
{
    ValueTask<Point> Echo(Point value);
}

[AttributeUsage(AttributeTargets.Struct)]
public sealed class FakePackableAttribute : Attribute { }

public sealed class FakeAdapter : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => "fake.adapter/v1";
    public string WireFormatId => "fake-wire/v1";
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FakeAdapter), \"fake.adapter/v1\", \"fake-wire/v1\", SelectorAttributeType = typeof(FakePackableAttribute))]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("CreateCodec<global::Point>()", StringComparison.Ordinal),
            "a selected Adapter must win for an unmanaged user-defined struct");
        Ensure(generated.Contains("__codec_value = codecs.GetCodec<global::Point>();", StringComparison.Ordinal),
            "an unmanaged request must resolve the selected Adapter Codec");
        Ensure(generated.Contains("__codec_value.Serialize(value.value, writer);", StringComparison.Ordinal),
            "an unmanaged request must be length-delimited through the selected Adapter Codec");
        return Task.CompletedTask;
    }

    [Test]
    public Task ConflictingSelectorRegistrationsShouldReportSharplink044()
    {
        var source = AddAssemblyAttributes(BuildSource("""
[AttributeUsage(AttributeTargets.Class)]
public sealed class SharedSelectorAttribute : Attribute { }

public sealed class FirstAdapter : AdapterBase { }
public sealed class SecondAdapter : AdapterBase { }
public abstract class AdapterBase : SharpLink.Abstractions.IRpcCodecAdapter
{
    public string AdapterId => GetType().Name;
    public string WireFormatId => GetType().Name;
    public SharpLink.Abstractions.IRpcCodecAdapterScope CreateScope() => throw new NotImplementedException();
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(FirstAdapter), \"first/v1\", \"wire/v1\", SelectorAttributeType = typeof(SharedSelectorAttribute))]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(SecondAdapter), \"second/v1\", \"wire/v1\", SelectorAttributeType = typeof(SharedSelectorAttribute))]");

        EnsureRuleCount(source, "SHARPLINK044", 1);
        return Task.CompletedTask;
    }
}
