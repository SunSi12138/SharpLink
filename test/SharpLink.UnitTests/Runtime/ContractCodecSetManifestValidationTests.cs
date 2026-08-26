using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.Server;

namespace SharpLink.UnitTests.Runtime;

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
        if (!RuntimeFeature.IsDynamicCodeSupported)
            return;

        var server = SharpLinkServerBuilder.Create()
            .UseTransport(new NoopListener())
            .Build();
        try
        {
            var assembly = CreateForeignManifestAssembly();
            var loadContext = AssemblyLoadContext.GetLoadContext(assembly) ?? AssemblyLoadContext.Default;
            Assembly? ResolveDynamicAssembly(AssemblyLoadContext _, AssemblyName requested)
                => string.Equals(requested.Name, assembly.GetName().Name, StringComparison.Ordinal)
                    ? assembly
                    : null;

            loadContext.Resolving += ResolveDynamicAssembly;
            try
            {
                var result = server.RegisterAssembly(assembly);

                Ensure(!result.Succeeded, "dynamic registration must reject a foreign Contract Codec set");
                Ensure(result.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                    "dynamic registration should return InvalidManifest");
                Ensure(result.Error?.Artifact == "ContractCodecSet",
                    $"dynamic registration should attribute the failure to the Contract Codec set; " +
                    $"artifact='{result.Error?.Artifact ?? "<null>"}', message='{result.Error?.Message ?? "<null>"}'");
                Ensure(result.Error.Message.Contains("foreign or undeclared Contract", StringComparison.Ordinal),
                    "dynamic registration should report the foreign Contract Codec set");
            }
            finally
            {
                loadContext.Resolving -= ResolveDynamicAssembly;
            }
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    private static Assembly CreateForeignManifestAssembly()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"SharpLink.ForeignContractCodecSet.{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("main");
        var manifestBuilder = module.DefineType(
            "GeneratedManifest",
            TypeAttributes.Public | TypeAttributes.Class,
            typeof(ForeignDynamicManifestBase));
        manifestBuilder.DefineDefaultConstructor(MethodAttributes.Public);
        var manifestType = manifestBuilder.CreateType() ??
            throw new InvalidOperationException("Could not create dynamic manifest type.");

        var locatorConstructor = typeof(SharpLinkGeneratedAssemblyManifestAttribute)
            .GetConstructor([typeof(Type)]) ?? throw new MissingMethodException(
                typeof(SharpLinkGeneratedAssemblyManifestAttribute).FullName,
                ".ctor(Type)");
        assembly.SetCustomAttribute(new CustomAttributeBuilder(locatorConstructor, [manifestType]));
        return assembly;
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

    private interface IForeignContract
    {
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
            [new(typeof(IForeignContract), HasCompileTimePolicy: true, Codecs: [], Dependencies: [])];
        public IReadOnlyList<string> Dependencies => [];
    }

    public class ForeignDynamicManifestBase : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => GetType().Assembly;
        public string CompileTimeDescriptor => "test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<SharpLinkGeneratedContractCodecSet> ContractCodecSets =>
            [new(typeof(IForeignContract), HasCompileTimePolicy: true, Codecs: [], Dependencies: [])];
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
