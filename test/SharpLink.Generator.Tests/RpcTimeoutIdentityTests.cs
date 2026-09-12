using System;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task EquivalentTimeoutTicksShouldShareRpcIdentity()
    {
        var exact = GenerateTimeoutIdentityManifest("1.0");
        var sameTick = GenerateTimeoutIdentityManifest("1.00000000001");

        Ensure(
            ExtractGeneratedRpcAssemblyHash(exact) == ExtractGeneratedRpcAssemblyHash(sameTick),
            "different Timeout attribute literals that normalize to the same TimeSpan tick must share RPC semantic identity");
        return Task.CompletedTask;
    }

    [Test]
    public Task DifferentTimeoutTicksShouldChangeRpcIdentity()
    {
        var exact = GenerateTimeoutIdentityManifest("1.0");
        var nextTick = GenerateTimeoutIdentityManifest("1.0000001");

        Ensure(
            ExtractGeneratedRpcAssemblyHash(exact) != ExtractGeneratedRpcAssemblyHash(nextTick),
            "a one-tick execution-policy difference must change RPC semantic identity");
        return Task.CompletedTask;
    }

    private static string GenerateTimeoutIdentityManifest(string timeoutSeconds)
    {
        var source = BuildSource($$"""
[SharpLink.Sdk.RpcContract]
public interface ITimeoutIdentityContract : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.Timeout({{timeoutSeconds}}d)]
    ValueTask<int> Echo(int value, CancellationToken cancellationToken);
}
""");

        return GenerateIdentityManifest(
            "TimeoutIdentityContracts",
            source,
            Platform.AnyCpu);
    }
}
