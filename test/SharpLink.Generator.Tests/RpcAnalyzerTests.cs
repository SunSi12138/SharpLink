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
    public Task TimeoutWithoutCancellationTokenShouldBeAllowed()
    {
        var source = BuildSource("""
public interface IHelloService : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.Timeout(1)]
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
""");

        var generated = RunGeneratorAndGetSources(source);
        var proxy = generated.FirstOrDefault(static text => text.Contains("IHelloService_Proxy"));
        if (proxy is null)
            throw new Exception("Expected generated proxy source.");
        Ensure(proxy.Contains("InvokeUnaryAsync"), "Unary invoker");
        Ensure(proxy.Contains("InvokeOneWayAsync"), "OneWay invoker");
        Ensure(proxy.Contains("InvokeClientStreamingAsync"), "ClientStreaming invoker");
        Ensure(proxy.Contains("InvokeServerStreamingAsync"), "ServerStreaming invoker");
        Ensure(proxy.Contains("InvokeDuplexStreamingAsync"), "DuplexStreaming invoker");
        Ensure(proxy.Contains("readonly struct __SharpLinkRequest_"), "Generated request struct");
        Ensure(proxy.Contains("IRpcCodec<__SharpLinkRequest_"), "Generated request codec");
        Ensure(!proxy.Contains("Action<IBufferWriter<byte>>"), "Captured payload delegate must not be generated");
        Ensure(!proxy.Contains("InvokeCancellableWithTimeoutAsync"), "Legacy combinatorial API must not be generated");
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

    public readonly record struct SharpLinkCallOptions;
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
