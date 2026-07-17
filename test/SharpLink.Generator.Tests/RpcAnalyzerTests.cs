using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpLink.Generator.Tests;

public class RpcAnalyzerTests
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
    public Task MultipleCancellationTokensShouldReportSharplink002()
    {
        var source = BuildSource("""
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, CancellationToken ct1, CancellationToken ct2);
}
""");
        source = source.Replace("public interface IHelloService : SharpLink.Sdk.IService", "[SharpLink.Sdk.RpcContract]\npublic interface IHelloService : SharpLink.Sdk.IService");

        EnsureHasRule(source, "SHARPLINK002");
        return Task.CompletedTask;
    }

    [Test]
    public Task TooManyStreamParametersShouldReportSharplink003()
    {
        var parameters = string.Join(", ",
            Enumerable.Range(0, 128).Select(i => $"IAsyncEnumerable<int> p{i}"));
        var source = BuildSource($$"""
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo({{parameters}});
}
""");
        source = source.Replace("public interface IHelloService : SharpLink.Sdk.IService", "[SharpLink.Sdk.RpcContract]\npublic interface IHelloService : SharpLink.Sdk.IService");

        EnsureHasRule(source, "SHARPLINK003");
        return Task.CompletedTask;
    }

    [Test]
    public Task MissingCancellationTokenShouldReportSharplink004()
    {
        var source = BuildSource("""
public interface IHelloService : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.Timeout(1)]
    ValueTask<int> Echo(int value);
}
""");
        source = source.Replace("public interface IHelloService : SharpLink.Sdk.IService", "[SharpLink.Sdk.RpcContract]\npublic interface IHelloService : SharpLink.Sdk.IService");

        EnsureHasRule(source, "SHARPLINK004");
        return Task.CompletedTask;
    }

    [Test]
    public Task ExplicitNonCancellableShouldSuppressSharplink004()
    {
        var source = BuildSource("""
public interface IHelloService : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable]
    ValueTask<int> Echo(int value);
}
""");
        source = source.Replace("public interface IHelloService : SharpLink.Sdk.IService", "[SharpLink.Sdk.RpcContract]\npublic interface IHelloService : SharpLink.Sdk.IService");

        EnsureDoesNotHaveRule(source, "SHARPLINK004");
        return Task.CompletedTask;
    }

    [Test]
    public Task MultipleCallOptionsShouldReportSharplink007()
    {
        var source = BuildSource("""
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(int value, SharpLink.Sdk.SharpLinkCallOptions first, SharpLink.Sdk.SharpLinkCallOptions second);
}
""");
        source = source.Replace("public interface IHelloService : SharpLink.Sdk.IService", "[SharpLink.Sdk.RpcContract]\npublic interface IHelloService : SharpLink.Sdk.IService");

        EnsureHasRule(source, "SHARPLINK007");
        return Task.CompletedTask;
    }

    [Test]
    public Task MisplacedControlParameterShouldReportSharplink008()
    {
        var source = BuildSource("""
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<int> Echo(SharpLink.Sdk.SharpLinkCallOptions options, int value, CancellationToken cancellationToken);
}
""");
        source = source.Replace("public interface IHelloService : SharpLink.Sdk.IService", "[SharpLink.Sdk.RpcContract]\npublic interface IHelloService : SharpLink.Sdk.IService");

        EnsureHasRule(source, "SHARPLINK008");
        return Task.CompletedTask;
    }

    [Test]
    public Task GenericMethodInIServiceShouldReportSharplink005Once()
    {
        var source = BuildSource("""
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<T> Echo<T>(T value);
}
""");
        source = source.Replace("public interface IHelloService : SharpLink.Sdk.IService", "[SharpLink.Sdk.RpcContract]\npublic interface IHelloService : SharpLink.Sdk.IService");

        var diagnostics = RunGenerator(source);
        var hits = diagnostics.Where(d => d.Id == "SHARPLINK005").ToArray();
        Ensure(hits.Length == 1, $"Expected exactly one SHARPLINK005, but got {hits.Length}.");
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
    public Task ProxyShouldUseFiveInvokerShapesWithoutCapturedPayloadDelegate()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<int> Unary(int value);
    [SharpLink.Sdk.Oneway]
    ValueTask Notify(string value);
    ValueTask<int> Upload(IAsyncEnumerable<int> values);
    IAsyncEnumerable<int> Download(int count);
    IAsyncEnumerable<int> Duplex(IAsyncEnumerable<int> values);
}

[SharpLink.Sdk.RpcService]
public sealed class HelloService : IHelloService
{
    public ValueTask<int> Unary(int value) => throw new NotImplementedException();
    public ValueTask Notify(string value) => throw new NotImplementedException();
    public ValueTask<int> Upload(IAsyncEnumerable<int> values) => throw new NotImplementedException();
    public IAsyncEnumerable<int> Download(int count) => throw new NotImplementedException();
    public IAsyncEnumerable<int> Duplex(IAsyncEnumerable<int> values) => throw new NotImplementedException();
}
""");

        var generated = RunGeneratorAndGetSources(source);
        var allGenerated = string.Join("\n", generated);
        var proxy = generated.FirstOrDefault(static text => text.Contains("IHelloService_Proxy"));
        if (proxy is null)
            throw new Exception("Expected generated proxy source.");
        Ensure(proxy.Contains("InvokeUnaryAsync"), "Unary invoker");
        Ensure(proxy.Contains("InvokeOneWayAsync"), "OneWay invoker");
        Ensure(proxy.Contains("InvokeClientStreamingAsync"), "ClientStreaming invoker");
        Ensure(proxy.Contains("InvokeServerStreamingAsync"), "ServerStreaming invoker");
        Ensure(proxy.Contains("InvokeDuplexStreamingAsync"), "DuplexStreaming invoker");
        Ensure(proxy.Contains("readonly struct __IHelloService_SharpLinkRequest_"), "Generated request struct");
        Ensure(proxy.Contains("IRpcCodec<__IHelloService_SharpLinkRequest_"), "Generated request codec");
        Ensure(allGenerated.Contains("Span<byte> tmp_"), "Segmented fixed-width arguments must use stack scratch");
        Ensure(!allGenerated.Contains("byte[] tmp_"), "Segmented fixed-width arguments must not allocate arrays");
        Ensure(!proxy.Contains("Action<IBufferWriter<byte>>"), "Captured payload delegate must not be generated");
        Ensure(!proxy.Contains("InvokeCancellableWithTimeoutAsync"), "Legacy combinatorial API must not be generated");
        return Task.CompletedTask;
    }

    [Test]
    public Task ReachableDtoShouldGenerateCodecAndManifest()
    {
        var source = BuildSource("""
public sealed record Address([property: SharpLink.Sdk.RpcMember(7)] string City);

public sealed class Person
{
    [SharpLink.Sdk.RpcRequired]
    public string Name { get; init; } = string.Empty;
    public int Age { get; init; }
    public Address Address { get; init; } = new Address(string.Empty);
    public List<string> Tags { get; init; } = new();
}

[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<Person> Echo(Person value);
}
""");

        var generated = RunGeneratorAndGetSources(source);
        var codecs = generated.FirstOrDefault(static text => text.Contains("__SharpLinkGeneratedCodecManifest"));
        if (codecs is null)
            throw new Exception("Expected generated DTO codec manifest source.");
        Ensure(codecs.Contains("IRpcCodec<global::Person>"), "Person codec");
        Ensure(codecs.Contains("IRpcCodec<global::Address>"), "nested record codec");
        Ensure(codecs.Contains("IRpcCodec<global::System.Collections.Generic.List<string>>"), "collection codec");
        Ensure(codecs.Contains("RpcGeneratedCodecRegistry.Register"), "manifest registration");
        Ensure(codecs.Contains("case 7U:"), "explicit field ID");
        Ensure(codecs.Contains("Missing required RPC member 'Name'"), "required member validation");
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
    public Task CyclicDtoGraphShouldReportSharplink010()
    {
        var source = BuildSource("""
public sealed class Node
{
    public Node? Next { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<Node> Echo(Node value);
}
""");

        EnsureHasRule(source, "SHARPLINK010");
        return Task.CompletedTask;
    }

    [Test]
    public Task DuplicateDtoMemberIdShouldReportSharplink011()
    {
        var source = BuildSource("""
public sealed class Collision
{
    [SharpLink.Sdk.RpcMember(1)] public int First { get; set; }
    [SharpLink.Sdk.RpcMember(1)] public int Second { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IHelloService : SharpLink.Sdk.IService
{
    ValueTask<Collision> Echo(Collision value);
}
""");

        EnsureHasRule(source, "SHARPLINK011");
        return Task.CompletedTask;
    }

    private static string BuildSource(string contract)
    {
        return $$"""
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SharpLink.Sdk
{
    public interface IService
    {
    }

    [AttributeUsage(AttributeTargets.Interface)]
    public sealed class RpcContractAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TimeoutAttribute : Attribute
    {
        public TimeoutAttribute(double seconds)
        {
        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class OnewayAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class NonCancellableAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RpcServiceAttribute : Attribute
    {
    }

    public readonly record struct SharpLinkCallOptions;

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class RpcSerializableAttribute : Attribute;

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class RpcMemberAttribute(int id) : Attribute
    {
        public int Id { get; } = id;
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class RpcIgnoreAttribute : Attribute;

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class RpcRequiredAttribute : Attribute;

    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
    public sealed class RpcExternalCodecAttribute : Attribute
    {
        public RpcExternalCodecAttribute() { }
        public RpcExternalCodecAttribute(Type type) { }
    }
}

{{contract}}
""";
    }

    private static void EnsureHasRule(string source, string ruleId)
    {
        var diagnostics = RunGenerator(source);
        var has = diagnostics.Any(d => d.Id == ruleId);
        Ensure(has, $"Expected diagnostic {ruleId}, but it was not reported.");
    }

    private static void EnsureDoesNotHaveRule(string source, string ruleId)
    {
        var diagnostics = RunGenerator(source);
        var has = diagnostics.Any(d => d.Id == ruleId);
        Ensure(!has, $"Did not expect diagnostic {ruleId}.");
    }

    private static ImmutableArray<Diagnostic> RunGenerator(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default);
        var compilation = CSharpCompilation.Create(
            assemblyName: "AnalyzerTestAssembly",
            syntaxTrees: [syntaxTree],
            references: GetPlatformReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        IIncrementalGenerator generator = new RpcGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult().Diagnostics;
    }

    private static string[] RunGeneratorAndGetSources(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default);
        var compilation = CSharpCompilation.Create(
            assemblyName: "GeneratorShapeTestAssembly",
            syntaxTrees: [syntaxTree],
            references: GetPlatformReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        IIncrementalGenerator generator = new RpcGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult().GeneratedTrees
            .Select(static tree => tree.GetText().ToString())
            .ToArray();
    }

    private static IEnumerable<MetadataReference> GetPlatformReferences()
    {
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (string.IsNullOrWhiteSpace(tpa))
            throw new Exception("TRUSTED_PLATFORM_ASSEMBLIES is unavailable.");

        return tpa.Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => MetadataReference.CreateFromFile(p));
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
