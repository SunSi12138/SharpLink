using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class GeneratedManifestCompatibilityTests
{
    [Test]
    public void ValidatorShouldRejectVersionBeforeReadingManifestShape()
    {
        var manifest = new ProbeManifest(
            apiVersion: SharpLinkGeneratedManifestVersions.Api - 1,
            protocolVersion: SharpLinkGeneratedManifestVersions.Protocol,
            ownerAssembly: typeof(GeneratedManifestCompatibilityTests).Assembly);

        var error = SharpLinkGeneratedManifestCompatibility.Validate(
            manifest,
            typeof(GeneratedManifestCompatibilityTests).Assembly);

        Ensure(error is not null, "incompatible manifest should be rejected");
        Ensure(error!.Code == SharpLinkAssemblyRegistrationErrorCode.IncompatibleManifest,
            "version mismatch should use the incompatible-manifest error code");
        Ensure(error.Message.Contains(
                $"API {manifest.ApiVersion}/{SharpLinkGeneratedManifestVersions.Api}",
                StringComparison.Ordinal),
            "diagnostic should carry incoming and required API versions");
        Ensure(error.IncomingAssembly == typeof(GeneratedManifestCompatibilityTests).Assembly.FullName,
            "diagnostic should identify the expected owner assembly");
        Ensure(manifest.ShapeReads == 0,
            "version rejection must happen before descriptor or Codec shape is read");
    }

    [Test]
    public void ValidatorShouldRejectOwnershipBeforeReadingManifestShape()
    {
        var manifest = new ProbeManifest(
            SharpLinkGeneratedManifestVersions.Api,
            SharpLinkGeneratedManifestVersions.Protocol,
            typeof(string).Assembly);

        var error = SharpLinkGeneratedManifestCompatibility.Validate(
            manifest,
            typeof(GeneratedManifestCompatibilityTests).Assembly);

        Ensure(error is not null, "foreign manifest owner should be rejected");
        Ensure(error!.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
            "ownership mismatch should use the invalid-manifest error code");
        Ensure(error.Message.Contains("does not match", StringComparison.Ordinal),
            "ownership diagnostic should state the mismatch");
        Ensure(manifest.ShapeReads == 0,
            "ownership rejection must happen before descriptor or Codec shape is read");
    }

    [Test]
    public void RuntimeContextShouldRejectVersionBeforePreparingGeneratedCodecs()
    {
        var manifest = new ProbeManifest(
            apiVersion: SharpLinkGeneratedManifestVersions.Api + 1,
            protocolVersion: SharpLinkGeneratedManifestVersions.Protocol,
            ownerAssembly: typeof(GeneratedManifestCompatibilityTests).Assembly);

        Exception? failure = null;
        try
        {
            using var _ = new SharpLinkRuntimeContextBuilder().Build([manifest]);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Ensure(failure is InvalidOperationException, "runtime build should reject the manifest");
        Ensure(failure!.Message.Contains("incompatible", StringComparison.OrdinalIgnoreCase),
            "runtime rejection should preserve the compatibility cause");
        Ensure(manifest.ShapeReads == 0,
            "runtime rejection must precede Codec enumeration and adapter-scope creation");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class ProbeManifest(
        int apiVersion,
        int protocolVersion,
        Assembly ownerAssembly) : ISharpLinkGeneratedAssemblyManifest
    {
        private int _shapeReads;

        public int ApiVersion => apiVersion;

        public int ProtocolVersion => protocolVersion;

        public string GeneratorVersion => "p3-preflight-test";

        public Assembly OwnerAssembly => ownerAssembly;

        public int ShapeReads => Volatile.Read(ref _shapeReads);

        public string CompileTimeDescriptor => ReadShape<string>();

        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts =>
            ReadShape<IReadOnlyList<SharpLinkGeneratedContractDescriptor>>();

        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services =>
            ReadShape<IReadOnlyList<SharpLinkGeneratedServiceDescriptor>>();

        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs =>
            ReadShape<IReadOnlyList<IRpcGeneratedCodecFactory>>();

        public IReadOnlyList<string> Dependencies => ReadShape<IReadOnlyList<string>>();

        private T ReadShape<T>()
        {
            Interlocked.Increment(ref _shapeReads);
            throw new InvalidOperationException("Manifest shape was read before compatibility preflight.");
        }
    }
}
