using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task Issue866GeneratedUtf16HelperMustEnforceWireLimitBeforeWrite()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class StringPayload { public string Text { get; set; } = ""; }
[SharpLink.Sdk.RpcContract]
public interface IStringPayloadContract : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable]
    ValueTask<StringPayload> Get();
}
""");
        var helper = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(helper.Contains("byteCount > RpcGeneratedCodecWire.MaximumStringPayloadBytes", StringComparison.Ordinal),
            "sizing and known-size writing must share the wire limit");
        Ensure(helper.Contains("SharpLinkErrorCode.ResourceExhausted", StringComparison.Ordinal),
            "oversize generated strings must use the same exception as RpcGeneratedCodecWire.WriteString");
        Ensure(helper.Contains("var byteCount = checked(value.Length * sizeof(char));", StringComparison.Ordinal),
            "sizing must retain checked multiplication");
        return Task.CompletedTask;
    }
}
