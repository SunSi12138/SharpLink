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
    public Task RpcContractShouldGenerateInheritedBaseMethods()
    {
        var source = BuildSource("""
public interface IBaseOperations
{
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
    ValueTask<int> Ping(int value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcContract]
public interface IDerivedService : SharpLink.Sdk.IService, IBaseOperations
{
    new ValueTask<int> Echo(int value, CancellationToken cancellationToken);
    ValueTask<int> Add(int left, int right, CancellationToken cancellationToken);
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("public global::System.Threading.Tasks.ValueTask<int> Ping(", StringComparison.Ordinal),
            "proxy should implement an inherited-only RPC method");
        Ensure(generated.Contains("impl.Ping(", StringComparison.Ordinal),
            "stub should dispatch an inherited-only RPC method");
        Ensure(CountOccurrences(generated, "public global::System.Threading.Tasks.ValueTask<int> Echo(") == 1,
            "a directly redeclared base method should be generated exactly once");
        return Task.CompletedTask;
    }

    [Test]
    public Task IncompatibleInheritedRpcRoutesShouldReportASpecificDiagnostic()
    {
        var source = BuildSource("""
public interface INumericBase
{
    ValueTask<int> Resolve(CancellationToken cancellationToken);
}

public interface ITextBase
{
    ValueTask<string> Resolve(CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcContract]
public interface IConflictingContract : SharpLink.Sdk.IService, INumericBase, ITextBase
{
}
""");

        EnsureRuleCount(source, "SHARPLINK057", 1);
        Ensure(!string.Join("\n", RunGeneratorAndGetSources(source)).Contains(
                "IConflictingContractProxy",
                StringComparison.Ordinal),
            "a conflicting inherited contract must not emit a broken Proxy");
        return Task.CompletedTask;
    }

    [Test]
    public Task ConflictingInheritedRpcPoliciesShouldReportASpecificDiagnostic()
    {
        var source = BuildSource("""
public interface IRetryingBase
{
    [SharpLink.Sdk.Timeout(1)]
    [SharpLink.Sdk.Idempotent]
    ValueTask<int> Resolve(int value, CancellationToken cancellationToken);
}

public interface INonRetryingBase
{
    [SharpLink.Sdk.Timeout(2)]
    ValueTask<int> Resolve(int value, CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcContract]
public interface IConflictingPolicyContract : SharpLink.Sdk.IService, IRetryingBase, INonRetryingBase
{
}
""");

        EnsureRuleCount(source, "SHARPLINK057", 1);
        Ensure(!string.Join("\n", RunGeneratorAndGetSources(source)).Contains(
                "IConflictingPolicyContractProxy",
                StringComparison.Ordinal),
            "conflicting inherited RPC policies must not emit contract artifacts");
        return Task.CompletedTask;
    }

    [Test]
    public Task DirectRedeclarationShouldCanonicalizeInheritedRpcSemantics()
    {
        var source = BuildSource("""
public interface IFireAndForgetBase
{
    [SharpLink.Sdk.Oneway]
    ValueTask Notify(CancellationToken cancellationToken);
}

public interface IAcknowledgedBase
{
    ValueTask Notify(CancellationToken cancellationToken);
}

[SharpLink.Sdk.RpcContract]
public interface ICanonicalContract : SharpLink.Sdk.IService, IFireAndForgetBase, IAcknowledgedBase
{
    new ValueTask Notify(CancellationToken cancellationToken);
}
""");

        EnsureDoesNotHaveRule(source, "SHARPLINK057");
        Ensure(string.Join("\n", RunGeneratorAndGetSources(source)).Contains(
                ": global::ICanonicalContract",
                StringComparison.Ordinal),
            "an explicit derived declaration must remain the canonical generated route");
        return Task.CompletedTask;
    }

    [Test]
    public Task RpcContractWithoutIServiceShouldReportSharplink006()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IHelloService
{
    ValueTask<int> Echo(int value);
}
""");

        EnsureHasRule(source, "SHARPLINK006");
        return Task.CompletedTask;
    }

    [Test]
    public Task ReferencedAssemblyManifestsShouldEmitDeterministicStaticBootstrapCalls()
    {
        var infrastructure = CreateManifestInfrastructureReference();
        var alpha = CreateGeneratedManifestReference(
            "AlphaServices",
            "AlphaManifest",
            "HiddenAlphaService",
            infrastructure);
        var zeta = CreateGeneratedManifestReference(
            "ZetaServices",
            "ZetaManifest",
            "HiddenZetaService",
            infrastructure);
        var legacy = CreateLegacyGeneratedManifestReference(infrastructure);
        var malformed = CreateMalformedManifestReference(infrastructure);
        var ordinary = CreateMetadataReference(
            "OrdinaryDependency",
            "namespace OrdinaryDependency { public sealed class OrdinaryType { } }");
        const string consumer = "namespace Consumer { internal sealed class Marker; }";

        var first = GetReferencedManifestBootstrap(
            RunGeneratorAndGetSources(consumer, infrastructure, zeta, ordinary, legacy, malformed, alpha));
        var second = GetReferencedManifestBootstrap(
            RunGeneratorAndGetSources(consumer, infrastructure, alpha, malformed, legacy, ordinary, zeta));

        Ensure(string.Equals(first, second, StringComparison.Ordinal),
            "referenced manifest bootstrap output must not depend on metadata-reference order");
        Ensure(CountOccurrences(first, ".Register();") == 2,
            "each current referenced generated manifest must receive exactly one bootstrap call");
        var alphaCall = first.IndexOf("global::SharpLink.Generated.AlphaManifest.Register();", StringComparison.Ordinal);
        var zetaCall = first.IndexOf("global::SharpLink.Generated.ZetaManifest.Register();", StringComparison.Ordinal);
        Ensure(alphaCall >= 0 && zetaCall > alphaCall,
            "bootstrap calls must use public fully qualified entry points in assembly-identity order");
        Ensure(!first.Contains("LegacyManifest", StringComparison.Ordinal),
            "legacy API 3 locators must not be bootstrapped into an API 4 process");
        Ensure(first.Contains("ModuleInitializer", StringComparison.Ordinal),
            "the consumer bootstrap must execute before application entry and server Build");
        Ensure(!first.Contains("OrdinaryDependency", StringComparison.Ordinal) &&
               !first.Contains("MalformedManifest", StringComparison.Ordinal) &&
               !first.Contains("HiddenAlphaService", StringComparison.Ordinal) &&
               !first.Contains("HiddenZetaService", StringComparison.Ordinal),
            "ordinary references and internal implementation types must not leak into the bootstrap");
        foreach (var forbidden in new[]
                 {
                     "Assembly.Load", "Assembly.LoadFrom", "GetCustomAttributes", "Directory.", "GetFiles("
                 })
        {
            Ensure(!first.Contains(forbidden, StringComparison.Ordinal),
                $"the static bootstrap must not use runtime discovery token '{forbidden}'");
        }

        EnsureGeneratorOutputCompiles(consumer, infrastructure, zeta, ordinary, legacy, malformed, alpha);
        return Task.CompletedTask;
    }

    [Test]
    public Task ContractsWithMatchingMethodHashesShouldGenerateDistinctHelperTypes()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IFirstService : SharpLink.Sdk.IService
{
    ValueTask<int> Add(int left, int right);
}

[SharpLink.Sdk.RpcContract]
public interface ISecondService : SharpLink.Sdk.IService
{
    ValueTask<int> Add(int left, int right);
}
""");

        var generated = RunGeneratorAndGetSources(source);
        var all = string.Join("\n", generated);
        Ensure(all.Contains("__IFirstService_SharpLinkRequest_"), "first contract helper type");
        Ensure(all.Contains("__ISecondService_SharpLinkRequest_"), "second contract helper type");
        return Task.CompletedTask;
    }

    [Test]
    public Task DuplicateStaticContractOwnersShouldReportSharplink021()
    {
        var sdk = CreateMetadataReference("SharpLink.Sdk", BuildSdkSource());
        var first = CreateMetadataReference("ContractOwnerA", BuildReferencedContractSource("ValueTask<int> Echo(int value);"), sdk);
        var second = CreateMetadataReference("ContractOwnerB", BuildReferencedContractSource("ValueTask<int> Echo(int value);"), sdk);

        EnsureHasRule(
            "namespace Consumer { public sealed class Marker; }",
            "SHARPLINK021",
            sdk,
            first,
            second);
        return Task.CompletedTask;
    }

    [Test]
    public Task ExplicitContractAssemblyFilterShouldExcludeUnselectedStaticConflicts()
    {
        var sdk = CreateMetadataReference("SharpLink.Sdk", BuildSdkSource());
        var first = CreateMetadataReference(
            "ContractOwnerA",
            BuildReferencedContractSource("ValueTask<int> Echo(int value);") +
            "\nnamespace ContractOwnerA { public sealed class Marker; }",
            sdk);
        var second = CreateMetadataReference(
            "ContractOwnerB",
            BuildReferencedContractSource("ValueTask<string> Echo(int value);") +
            "\nnamespace ContractOwnerB { public sealed class Marker; }",
            sdk);

        var diagnostics = RunGenerator(
            "[assembly: SharpLink.Sdk.SharpLinkRpcContracts(typeof(ContractOwnerA.Marker))]\n" +
            "namespace Consumer { public sealed class Marker; }",
            sdk,
            first,
            second);
        Ensure(!diagnostics.Any(static diagnostic =>
                diagnostic.Id is "SHARPLINK021" or "SHARPLINK022" or "SHARPLINK023"),
            $"Explicit contract scan filter must exclude unselected assemblies. Actual: {FormatDiagnostics(diagnostics)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task ExplicitEmptyContractAssemblyFilterShouldDisableReferencedContractScanning()
    {
        var sdk = CreateMetadataReference("SharpLink.Sdk", BuildSdkSource());
        var first = CreateMetadataReference("ContractOwnerA", BuildReferencedContractSource("ValueTask<int> Echo(int value);"), sdk);
        var second = CreateMetadataReference("ContractOwnerB", BuildReferencedContractSource("ValueTask<string> Echo(int value);"), sdk);

        var diagnostics = RunGenerator(
            "[assembly: SharpLink.Sdk.SharpLinkRpcContracts()]\n" +
            "namespace Consumer { public sealed class Marker; }",
            sdk,
            first,
            second);
        Ensure(!diagnostics.Any(static diagnostic =>
                diagnostic.Id is "SHARPLINK021" or "SHARPLINK022" or "SHARPLINK023"),
            $"An explicit empty contract filter must not fall back to automatic reference scanning. Actual: {FormatDiagnostics(diagnostics)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task NestedContractsShouldReceiveUniqueGeneratedPeerNames()
    {
        var source = BuildSource("""
namespace Nested
{
    public sealed class First
    {
        [SharpLink.Sdk.RpcContract]
        public interface IInner : SharpLink.Sdk.IService
        {
            ValueTask<int> Invoke(CancellationToken cancellationToken);
        }
    }

    public sealed class Second
    {
        [SharpLink.Sdk.RpcContract]
        public interface IInner : SharpLink.Sdk.IService
        {
            ValueTask<int> Invoke(CancellationToken cancellationToken);
        }
    }
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(CountOccurrences(generated, "IInner_Proxy") == 0,
            "nested contracts with the same simple name must not emit colliding top-level Proxy types");
        Ensure(generated.Contains(" : global::Nested.First.IInner", StringComparison.Ordinal) &&
               generated.Contains(" : global::Nested.Second.IInner", StringComparison.Ordinal),
            "both nested contracts must retain generated peers");
        return Task.CompletedTask;
    }

    [Test]
    public Task InaccessibleAndOpenNestedContractsShouldBeRejected()
    {
        var inaccessible = BuildSource("""
[SharpLink.Sdk.RpcContract]
interface IInternalContract : SharpLink.Sdk.IService
{
    ValueTask<int> Invoke(CancellationToken cancellationToken);
}

public sealed class Container
{
    [SharpLink.Sdk.RpcContract]
    private interface IPrivateContract : SharpLink.Sdk.IService
    {
        ValueTask<int> Invoke(CancellationToken cancellationToken);
    }
}
""");
        EnsureRuleCount(inaccessible, "SHARPLINK055", 2);

        var openNested = BuildSource("""
public sealed class GenericContainer<T>
{
    [SharpLink.Sdk.RpcContract]
    public interface IOpenContract : SharpLink.Sdk.IService
    {
        ValueTask<int> Invoke(CancellationToken cancellationToken);
    }
}
""");
        EnsureRuleCount(openNested, "SHARPLINK005", 1);
        return Task.CompletedTask;
    }
}
