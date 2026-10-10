using System;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task UnsafeBlitFactoryShouldCarryFinalizedAbiWithoutCatalogResolution()
    {
        var source = BuildSource("""
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct RawPayload
{
    public System.DateTimeOffset Stamp;
}

[SharpLink.Sdk.RpcContract]
public interface IRawService : SharpLink.Sdk.IService
{
    ValueTask<RawPayload> Echo(RawPayload value, CancellationToken cancellationToken);
}
""");
        var sources = RunGeneratorAndGetSources(source);
        var generated = string.Join("\n", sources);
        Ensure(generated.Contains("GetGeneratedUnsafeBlitCodec<global::RawPayload>(new SharpLinkGeneratedUnsafeBlitRequirement(8, true))", StringComparison.Ordinal),
            "the generated factory must carry its finalized platform requirement directly");
        Ensure(generated.Contains("provider is not IRpcGeneratedUnsafeBlitCodecProvider", StringComparison.Ordinal),
            "providers without generated validation capability must fail closed");
        Ensure(!generated.Contains("SharpLinkGeneratedUnsafeBlitCatalog", StringComparison.Ordinal),
            "generated resolution must not publish or query process-global ABI metadata");
        return Task.CompletedTask;
    }
    [Test]
    public Task RequestOnlyUnsafeBlitShouldResolveGeneratedFactory()
    {
        var source = BuildSource("""
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct RequestOnlyPayload { public long Value; }

[SharpLink.Sdk.RpcContract]
public interface IRequestOnlyService : SharpLink.Sdk.IService
{
    ValueTask Submit(RequestOnlyPayload value, CancellationToken cancellationToken);
}
""");
        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(generated.Contains("__codec_value = codecs.GetCodec<global::RequestOnlyPayload>();", StringComparison.Ordinal),
            "request-only raw payloads must resolve their owner-local factory in the request codec constructor");
        Ensure(generated.Contains("GetGeneratedUnsafeBlitCodec<global::RequestOnlyPayload>", StringComparison.Ordinal),
            "the request-only type must have a direct generated ABI factory");
        return Task.CompletedTask;
    }

}
