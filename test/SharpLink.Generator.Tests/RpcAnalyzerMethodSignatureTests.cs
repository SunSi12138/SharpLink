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
    public Task InvalidReturnTypeShouldReportSharplink001()
    {
        var source = BuildSource("""
public interface IHelloService : SharpLink.Sdk.IService
{
    int Echo(int value);
}
""");
        source = source.Replace("public interface IHelloService : SharpLink.Sdk.IService", "[SharpLink.Sdk.RpcContract]\npublic interface IHelloService : SharpLink.Sdk.IService");

        EnsureHasRule(source, "SHARPLINK001");
        return Task.CompletedTask;
    }

    [Test]
    public Task TaskPayloadNamedValueTaskShouldKeepOuterTaskSemantics()
    {
        var source = BuildSource("""
public sealed class ValueTaskPayload
{
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface ITaskPayloadContract : SharpLink.Sdk.IService
{
    Task<ValueTaskPayload> Echo(ValueTaskPayload value, CancellationToken cancellationToken);
}
""");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        var proxyStart = generated.IndexOf(
            "public global::System.Threading.Tasks.Task<global::ValueTaskPayload> Echo(",
            StringComparison.Ordinal);
        var proxyEnd = proxyStart < 0
            ? -1
            : generated.IndexOf("\n    }", proxyStart, StringComparison.Ordinal);
        Ensure(proxyStart >= 0 && proxyEnd > proxyStart &&
               generated.AsSpan(proxyStart, proxyEnd - proxyStart).Contains(".AsTask();", StringComparison.Ordinal),
            "Task<T> Proxy emission must convert the channel ValueTask using outer Task semantics");
        Ensure(generated.Contains(
                "__SerializeResponse(pending.GetAwaiter().GetResult(), false, __responseCodec_",
                StringComparison.Ordinal),
            "Task<T> Stub emission must use Task result semantics even when T contains 'ValueTask'");
        Ensure(generated.Contains(
                "return __AwaitTaskResultAsync(pending, false, __responseCodec_",
                StringComparison.Ordinal),
            "Task<T> Stub emission must await the outer Task type");
        Ensure(!generated.Contains("Serialize(pending.Result, output)", StringComparison.Ordinal),
            "Task<T> must not use the ValueTask-only Result path");
        return Task.CompletedTask;
    }

    [Test]
    public Task MisplacedControlParameterShouldReportSharplink008()
    {
        var source = BuildSource("""
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(CancellationToken cancellationToken, int value);
}
""");
        source = source.Replace("public interface IHelloService : SharpLink.Sdk.IService", "[SharpLink.Sdk.RpcContract]\npublic interface IHelloService : SharpLink.Sdk.IService");

        EnsureHasRule(source, "SHARPLINK008");
        return Task.CompletedTask;
    }

    [Test]
    public Task AmbiguousAndInaccessibleConstructorsShouldReportSharplink019()
    {
        var source = BuildSource("""
public sealed class FirstDependency;
public sealed class SecondDependency;

[SharpLink.Sdk.RpcContract]
public interface IAmbiguousContract : SharpLink.Sdk.IService
{
    ValueTask<int> Get(int value);
}

[SharpLink.Sdk.RpcContract]
public interface IInaccessibleContract : SharpLink.Sdk.IService
{
    ValueTask<int> Get(int value);
}

[SharpLink.Sdk.RpcService]
public sealed class AmbiguousConstructorService : IAmbiguousContract
{
    public AmbiguousConstructorService(FirstDependency dependency) { }
    public AmbiguousConstructorService(SecondDependency dependency) { }
    public ValueTask<int> Get(int value) => new(value);
}

[SharpLink.Sdk.RpcService]
public sealed class InaccessibleConstructorService : IInaccessibleContract
{
    private InaccessibleConstructorService() { }
    public ValueTask<int> Get(int value) => new(value);
}
""");

        EnsureRuleCount(source, "SHARPLINK019", 2);
        return Task.CompletedTask;
    }

    [Test]
    public Task SanitizedHintNamesShouldRemainUnique()
    {
        var source = BuildSource("""
namespace A.B
{
    [SharpLink.Sdk.RpcContract]
    public interface IC : SharpLink.Sdk.IService
    {
        ValueTask<int> Invoke(CancellationToken cancellationToken);
    }
}

namespace A
{
    [SharpLink.Sdk.RpcContract]
    public interface B_IC : SharpLink.Sdk.IService
    {
        ValueTask<int> Invoke(CancellationToken cancellationToken);
    }
}
""");

        var diagnostics = RunGenerator(source);
        Ensure(!diagnostics.Any(static diagnostic => diagnostic.Id == "CS8785"),
            $"Distinct fully-qualified contracts must not collide after hint-name sanitization. Actual: {FormatDiagnostics(diagnostics)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task KeywordRpcIdentifiersShouldEmitValidCSharpSyntax()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IKeywordContract : SharpLink.Sdk.IService
{
    ValueTask<int> @class(int @event, CancellationToken @default);
}
""");

        var generated = RunGeneratorAndGetSources(source);
        var syntaxErrors = generated
            .SelectMany(static text => CSharpSyntaxTree.ParseText(text).GetDiagnostics())
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Ensure(syntaxErrors.Length == 0,
            $"Keyword RPC identifiers must remain escaped in generated source. Actual: {FormatDiagnostics(syntaxErrors)}");
        return Task.CompletedTask;
    }

    [Test]
    public Task ConflictingStaticMethodDescriptorsShouldReportSharplink022()
    {
        var sdk = CreateMetadataReference("SharpLink.Sdk", BuildSdkSource());
        var first = CreateMetadataReference("MethodOwnerA", BuildReferencedContractSource("ValueTask<int> Echo(int value);"), sdk);
        var second = CreateMetadataReference("MethodOwnerB", BuildReferencedContractSource("ValueTask<string> Echo(int value);"), sdk);

        EnsureHasRule(
            "namespace Consumer { public sealed class Marker; }",
            "SHARPLINK022",
            sdk,
            first,
            second);
        return Task.CompletedTask;
    }

    [Test]
    public Task ClusterRouteShouldGenerateDeterministicSeparateManifest()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IOrdersService : SharpLink.Sdk.IService
{
    ValueTask<int> GetAsync(int value, CancellationToken cancellationToken);
}
""");
        source = AddAssemblyAttribute(
            source,
            "[assembly: SharpLink.Sdk.SharpLinkClusterContractAssembly(\"orders\", typeof(IOrdersService))]");

        var generated = RunGeneratorAndGetSources(source);
        var route = generated.Single(text => text.Contains("GeneratedClusterRouteManifest", StringComparison.Ordinal));
        Ensure(route.Contains("new SharpLinkClusterKey(\"orders\")", StringComparison.Ordinal),
            "cluster route should preserve the declared key");
        Ensure(route.Contains("SharpLinkGeneratedClusterRouteCatalog.Register", StringComparison.Ordinal),
            "cluster route manifest should register from a module initializer");
        Ensure(route.Contains("System.Array.AsReadOnly(__routes)", StringComparison.Ordinal),
            "cluster route manifest must not expose its generated array");
        return Task.CompletedTask;
    }

    [Test]
    public Task InvalidClusterRouteKeyShouldReportSharplink038()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IOrdersService : SharpLink.Sdk.IService
{
    ValueTask<int> GetAsync(int value, CancellationToken cancellationToken);
}
""");
        source = AddAssemblyAttribute(
            source,
            "[assembly: SharpLink.Sdk.SharpLinkClusterContractAssembly(\"bad key\", typeof(IOrdersService))]");

        EnsureHasRule(source, "SHARPLINK038");
        return Task.CompletedTask;
    }

    [Test]
    public Task ConflictingClusterRouteShouldReportSharplink039()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IOrdersService : SharpLink.Sdk.IService
{
    ValueTask<int> GetAsync(int value, CancellationToken cancellationToken);
}
""");
        source = AddAssemblyAttribute(
            source,
            "[assembly: SharpLink.Sdk.SharpLinkClusterContractAssembly(\"orders\", typeof(IOrdersService))]\n" +
            "[assembly: SharpLink.Sdk.SharpLinkClusterContractAssembly(\"payments\", typeof(IOrdersService))]");

        EnsureHasRule(source, "SHARPLINK039");
        return Task.CompletedTask;
    }

    [Test]
    public Task NullRouteMarkerShouldReportSharplink041()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IOrdersService : SharpLink.Sdk.IService
{
    ValueTask<int> GetAsync(int value, CancellationToken cancellationToken);
}
""");
        source = AddAssemblyAttribute(
            source,
            "[assembly: SharpLink.Sdk.SharpLinkClusterContractAssembly(\"orders\", null)]");

        EnsureHasRule(source, "SHARPLINK041");
        return Task.CompletedTask;
    }

    [Test]
    public Task EmptyInvocationCategoriesMustUseStructuredUnimplemented()
    {
        var responseOnly = string.Join("\n", RunGeneratorAndGetSources(BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IResponseOnlyContract : SharpLink.Sdk.IService
{
    ValueTask<int> Get(CancellationToken cancellationToken);
}
""")));
        var noResponseOnly = string.Join("\n", RunGeneratorAndGetSources(BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface INoResponseOnlyContract : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.Oneway]
    ValueTask Notify(CancellationToken cancellationToken);
}
""")));

        Ensure(!responseOnly.Contains("RpcException", StringComparison.Ordinal) &&
               !noResponseOnly.Contains("RpcException", StringComparison.Ordinal),
            "empty invocation categories must not emit the legacy unstructured exception");
        Ensure(responseOnly.Contains("SharpLinkErrorCode.Unimplemented", StringComparison.Ordinal) &&
               noResponseOnly.Contains("SharpLinkErrorCode.Unimplemented", StringComparison.Ordinal),
            "both empty invocation categories must return structured Unimplemented");
        return Task.CompletedTask;
    }
}
