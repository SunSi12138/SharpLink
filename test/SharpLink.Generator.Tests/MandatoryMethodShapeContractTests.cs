using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpLink.Generator.Tests;

public sealed class MandatoryMethodShapeContractTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void ActualStubContractShouldRequireCustomMethodResolution(bool implementsResolver)
    {
        // Copy the production interface through MSBuild rather than mirroring its signature:
        // changing the real member back to a default implementation must fail this regression.
        var actualContract = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "Contracts", "IRpcStub.cs"));
        var resolver = implementsResolver
            ? "public RpcMethodShape ResolveMethodShape(long methodHash) => default;"
            : string.Empty;
        var compilation = GeneratorTestHarness.CreateCompilation("MandatoryMethodShapeContract", $$"""
global using System;
global using System.Buffers;
global using System.Threading;
global using System.Threading.Tasks;
using SharpLink.Abstractions;

namespace SharpLink.Abstractions
{
    public readonly struct RpcMethodShape { }
    public readonly struct RpcMethodDescriptor
    {
        public static RpcMethodDescriptor FromShape(long contractId, long methodId, RpcMethodShape shape) => default;
    }
    public interface IRpcGeneratedServerBridge { }
}

public sealed class CustomStub : IRpcStub
{
    public long InterfaceHash => 1;
    {{resolver}}
    public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge,
        long methodHash, long requestId, ReadOnlySequence<byte> args) => default;
    public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge,
        long methodHash, long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken) => default;
    public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge,
        long methodHash, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output) => default;
    public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge,
        long methodHash, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output,
        CancellationToken cancellationToken) => default;
}
""").AddSyntaxTrees(CSharpSyntaxTree.ParseText(actualContract));
        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        var errors = result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (implementsResolver)
        {
            if (!result.Success)
                throw new Exception("A complete custom stub must compile: " + string.Join("; ", errors.Select(d => d.ToString())));
        }
        else if (result.Success || errors.Length != 1 || errors[0].Id != "CS0535" ||
                 !errors[0].GetMessage().Contains("ResolveMethodShape", StringComparison.Ordinal))
        {
            throw new Exception("A custom stub without method resolution must fail only for the missing resolver: " +
                string.Join("; ", errors.Select(d => d.ToString())));
        }
    }
}
