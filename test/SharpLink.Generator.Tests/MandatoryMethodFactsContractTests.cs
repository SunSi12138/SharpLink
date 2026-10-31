using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpLink.Generator.Tests;

public class MandatoryMethodFactsContractTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void RealStubInterfaceMustRequireCompleteFacts(bool suppliesFacts)
    {
        var contract = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "IRpcStub.source.cs"));
        var resolver = suppliesFacts
            ? "public RpcMethodFacts ResolveMethodFacts(long id) => RpcMethodFacts.UnknownMethod;"
            : string.Empty;
        var compilation = GeneratorTestHarness.CreateCompilation("ExplicitCompleteFactsContract", $$"""
global using System;
global using System.Buffers;
global using System.Threading;
global using System.Threading.Tasks;
using SharpLink.Abstractions;
namespace SharpLink.Abstractions
{
    public sealed class RpcMethodFacts { public static RpcMethodFacts UnknownMethod { get; } = new(); }
    public interface IRpcGeneratedServerBridge { }
}
public sealed class CustomStub : IRpcStub
{
    {{resolver}}
    public long InterfaceHash => 1;
    public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge,
        RpcMethodFacts method, long requestId, ReadOnlySequence<byte> args) => default;
    public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge,
        RpcMethodFacts method, long requestId, ReadOnlySequence<byte> args, CancellationToken token) => default;
    public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge,
        RpcMethodFacts method, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output) => default;
    public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge,
        RpcMethodFacts method, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output,
        CancellationToken token) => default;
}
""").AddSyntaxTrees(CSharpSyntaxTree.ParseText(contract));
        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        var errors = result.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error).ToArray();
        if (suppliesFacts && !result.Success)
            throw new InvalidOperationException("Complete custom stub must compile: " + string.Join("; ", errors.Select(item => item.ToString())));
        if (!suppliesFacts && (result.Success || errors.Length != 1 || errors[0].Id != "CS0535" ||
                !errors[0].GetMessage().Contains("ResolveMethodFacts", StringComparison.Ordinal)))
            throw new InvalidOperationException("Missing facts must fail only for the required resolver: " + string.Join("; ", errors.Select(item => item.ToString())));
    }
}
