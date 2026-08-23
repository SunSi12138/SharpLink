using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task GeneratedStubShouldGateDeadlineAfterDecodeBeforeBusinessInvocation()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class Payload
{
    public string Value { get; set; } = string.Empty;
}

[SharpLink.Sdk.RpcContract]
public interface IDeadlineGateService : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.Timeout(1)]
    [SharpLink.Sdk.NonCancellable]
    ValueTask<int> Invoke(Payload value);
}
""");

        var stub = RunGeneratorAndGetSources(source)
            .Single(static text => text.Contains("private sealed class __Stub_", StringComparison.Ordinal));
        var decodeIndex = stub.IndexOf(".Deserialize(in seq_value)", StringComparison.Ordinal);
        var deadlineGateIndex = stub.IndexOf("bridge.ThrowIfDeadlineExceeded();", StringComparison.Ordinal);
        var invocationIndex = stub.IndexOf("impl.Invoke(", StringComparison.Ordinal);

        Ensure(decodeIndex >= 0,
            "the regression fixture must deserialize the request through a generated Codec");
        Ensure(deadlineGateIndex > decodeIndex,
            "the server deadline gate must run after request argument deserialization");
        Ensure(invocationIndex > deadlineGateIndex,
            "the server deadline gate must run immediately before entering the business implementation");
        return Task.CompletedTask;
    }
}
