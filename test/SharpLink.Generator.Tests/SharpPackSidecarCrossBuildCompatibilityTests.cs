using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public void SharpPackSidecarsFromIndependentBuildsShouldCrossDecode()
    {
        _ = typeof(global::SharpPack.SharpPackSerializer).Assembly;

        var producer = CompileSharpPackCrossBuildArtifact("SharpPackSidecarProducer");
        var consumer = CompileSharpPackCrossBuildArtifact("SharpPackSidecarConsumer");

        Ensure(string.Equals(producer.CodecHash, consumer.CodecHash, StringComparison.Ordinal),
            "independent builds of the same external sidecar shape must negotiate the same CodecHash");

        var producerContext = new CrossBuildLoadContext(producer.VendorAssembly);
        var consumerContext = new CrossBuildLoadContext(consumer.VendorAssembly);
        try
        {
            var producerAssembly = LoadAssembly(producerContext, producer.HostAssembly);
            var consumerAssembly = LoadAssembly(consumerContext, consumer.HostAssembly);

            var producerBytes = InvokeProduce(producerAssembly);
            var consumedByConsumer = InvokeConsume(consumerAssembly, producerBytes);
            Ensure(string.Equals(consumedByConsumer, CrossBuildExpectedValue, StringComparison.Ordinal),
                "consumer build must decode bytes emitted by the independently generated producer sidecar");

            var consumerBytes = InvokeProduce(consumerAssembly);
            var consumedByProducer = InvokeConsume(producerAssembly, consumerBytes);
            Ensure(string.Equals(consumedByProducer, CrossBuildExpectedValue, StringComparison.Ordinal),
                "producer build must decode bytes emitted by the independently generated consumer sidecar");
        }
        finally
        {
            producerContext.Unload();
            consumerContext.Unload();
        }
    }

    private const string CrossBuildExpectedValue = "42|payload|7|1,2,3";

    private static CrossBuildArtifact CompileSharpPackCrossBuildArtifact(string assemblyName)
    {
        const string vendorSource = """
using System.Collections.Generic;

namespace Vendor;

public sealed class ExternalChild
{
    public int Value { get; set; }
}

public sealed class ExternalRequest
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public ExternalChild? Child { get; set; }
    public List<int>? Values { get; set; }
}
""";

        var vendorCompilation = CSharpCompilation.Create(
            "Vendor.Models",
            [CSharpSyntaxTree.ParseText(vendorSource, CSharpParseOptions.Default)],
            GeneratorTestHarness.GetPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var vendorBytes = EmitCompilation(vendorCompilation, "external metadata assembly");
        var vendorReference = MetadataReference.CreateFromImage(vendorBytes);

        var source = BuildSharpPackContractSource(
            """
    global::System.Threading.Tasks.Task<Vendor.ExternalRequest> EchoAsync(
        Vendor.ExternalRequest request,
        global::System.Threading.CancellationToken cancellationToken);
""",
            """
public sealed class CrossBuildSharpPackScope :
    global::SharpLink.Abstractions.IRpcCodecAdapterScope,
    global::SharpLink.Serializer.SharpPack.ISharpPackRpcCodecAdapterScopeConfiguration
{
    public global::SharpPack.SharpPackSerializerContext Context { get; private set; } = null!;

    public void Configure(
        string configurationId,
        global::System.Action<global::SharpPack.SharpPackSerializerContextBuilder> configure)
    {
        _ = configurationId;
        var builder = new global::SharpPack.SharpPackSerializerContextBuilder();
        configure(builder);
        Context = builder.Build();
    }

    public global::SharpLink.Abstractions.IRpcCodec<T> CreateCodec<T>()
        => throw new global::System.NotSupportedException();

    public void Dispose() { }
}

public static class CrossBuildBridge
{
    public static byte[] Produce()
    {
        using var scope = new CrossBuildSharpPackScope();
        global::SharpLink.Generated.__SharpLinkGeneratedSharpPackIntegration.Configure(scope);
        var value = new global::Vendor.ExternalRequest
        {
            Id = 42,
            Name = "payload",
            Child = new global::Vendor.ExternalChild { Value = 7 },
            Values = new global::System.Collections.Generic.List<int> { 1, 2, 3 },
        };
        return global::SharpPack.SharpPackSerializer.Serialize(value, scope.Context);
    }

    public static string Consume(byte[] bytes)
    {
        using var scope = new CrossBuildSharpPackScope();
        global::SharpLink.Generated.__SharpLinkGeneratedSharpPackIntegration.Configure(scope);
        var value = global::SharpPack.SharpPackSerializer.Deserialize<global::Vendor.ExternalRequest>(
            bytes,
            scope.Context) ?? throw new global::System.InvalidOperationException("payload decoded as null");
        var child = value.Child?.Value ?? -1;
        var values = value.Values is null
            ? string.Empty
            : string.Join(",", value.Values);
        return $"{value.Id}|{value.Name}|{child}|{values}";
    }
}
""");

        var result = RunSharpPackAndCompile(assemblyName, source, [vendorReference]);
        EnsureNoSharpPackErrors(result);
        var codecHash = GetSharpPackCodecHash(
            result.DriverRunResult,
            "global::Vendor.ExternalRequest");
        var hostBytes = EmitCompilation(result.OutputCompilation, "generated sidecar host assembly");
        return new CrossBuildArtifact(vendorBytes, hostBytes, codecHash);
    }

    private static byte[] EmitCompilation(Compilation compilation, string description)
    {
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        if (!emit.Success)
        {
            throw new Exception(
                $"Failed to emit {description}:{Environment.NewLine}" +
                string.Join(
                    Environment.NewLine,
                    emit.Diagnostics
                        .Where(static item => item.Severity == DiagnosticSeverity.Error)
                        .Select(static item => item.ToString())));
        }
        return stream.ToArray();
    }

    private static Assembly LoadAssembly(AssemblyLoadContext context, byte[] image)
    {
        using var stream = new MemoryStream(image, writable: false);
        return context.LoadFromStream(stream);
    }

    private static byte[] InvokeProduce(Assembly assembly)
    {
        var bridge = assembly.GetType("CrossBuildBridge", throwOnError: true)!;
        var method = bridge.GetMethod("Produce", BindingFlags.Public | BindingFlags.Static)
            ?? throw new Exception("CrossBuildBridge.Produce was not emitted.");
        return (byte[])(method.Invoke(null, null)
            ?? throw new Exception("CrossBuildBridge.Produce returned null."));
    }

    private static string InvokeConsume(Assembly assembly, byte[] bytes)
    {
        var bridge = assembly.GetType("CrossBuildBridge", throwOnError: true)!;
        var method = bridge.GetMethod("Consume", BindingFlags.Public | BindingFlags.Static)
            ?? throw new Exception("CrossBuildBridge.Consume was not emitted.");
        return (string)(method.Invoke(null, [bytes])
            ?? throw new Exception("CrossBuildBridge.Consume returned null."));
    }

    private sealed class CrossBuildLoadContext(byte[] vendorAssembly)
        : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, "Vendor.Models", StringComparison.Ordinal))
            {
                using var stream = new MemoryStream(vendorAssembly, writable: false);
                return LoadFromStream(stream);
            }

            try
            {
                return Default.LoadFromAssemblyName(assemblyName);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }
    }

    private sealed record CrossBuildArtifact(
        byte[] VendorAssembly,
        byte[] HostAssembly,
        string CodecHash);
}
