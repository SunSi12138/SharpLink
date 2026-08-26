using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;
using SharpLink.Abstractions;
using SharpLink.RollbackPlugin;
using SharpLink.Runtime;
using SharpLink.Server;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel]
public class ContractCodecSetManifestValidationTests
{
    [Test]
    public void CatalogRuntimeContextShouldRejectForeignContractCodecSetBeforeAdoption()
    {
        var manifest = new ForeignCatalogManifest();
        SharpLinkGeneratedAssemblyCatalog.Register(manifest);
        try
        {
            var failure = Capture(() => new SharpLinkRuntimeContextBuilder().Build());
            Ensure(failure is InvalidOperationException, "catalog preparation must reject the malformed manifest");
            Ensure(failure.Message.Contains("foreign or undeclared Contract", StringComparison.Ordinal),
                "catalog preparation should report the foreign Contract Codec set");
        }
        finally
        {
            RollbackTestIsolation.RemoveManifestFromCatalog(manifest);
        }
    }

    [Test]
    public async Task DynamicRegistrationShouldRejectForeignContractCodecSetBeforeAdoption()
    {
        await RollbackState.TestIsolation.WaitAsync();
        var server = SharpLinkServerBuilder.Create()
            .UseTransport(new NoopListener())
            .Build();
        try
        {
            Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", "1");
            Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_FOREIGN_CONTRACT_CODEC_SET", "1");

            var result = server.RegisterAssembly(typeof(RollbackMarker).Assembly);

            Ensure(!result.Succeeded, "dynamic registration must reject a foreign Contract Codec set");
            Ensure(result.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                "dynamic registration should return InvalidManifest");
            Ensure(result.Error?.Artifact == "ContractCodecSet",
                $"dynamic registration should attribute the failure to the Contract Codec set; " +
                $"artifact='{result.Error?.Artifact ?? "<null>"}', message='{result.Error?.Message ?? "<null>"}'");
            Ensure(result.Error?.Message.Contains("foreign or undeclared Contract", StringComparison.Ordinal) == true,
                "dynamic registration should report the foreign Contract Codec set");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_FOREIGN_CONTRACT_CODEC_SET", null);
            Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", null);
            try { await server.DisposeAsync(); } catch { }
            RollbackState.TestIsolation.Release();
        }
    }

    private static Exception Capture(Func<SharpLinkRuntimeContext> action)
    {
        try
        {
            using var context = action();
            throw new Exception("expected malformed manifest preparation to fail");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class ForeignCatalogManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(ContractCodecSetManifestValidationTests).Assembly;
        public string CompileTimeDescriptor => "test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<SharpLinkGeneratedContractCodecSet> ContractCodecSets =>
            [new(typeof(ISharpLinkGeneratedAssemblyManifest), HasCompileTimePolicy: true, Codecs: [], Dependencies: [])];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class NoopListener : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => null;

        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
