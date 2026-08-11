using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class GeneratedManifestCompatibilityTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(5)]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(int.MinValue)]
    [Arguments(int.MaxValue)]
    public void ValidatorShouldRejectEveryUnsupportedApiBeforeReadingManifestShape(int apiVersion)
    {
        var manifest = new ProbeManifest(
            apiVersion,
            SharpLinkGeneratedManifestVersions.Protocol,
            typeof(GeneratedManifestCompatibilityTests).Assembly);

        var error = SharpLinkGeneratedManifestCompatibility.Validate(
            manifest,
            typeof(GeneratedManifestCompatibilityTests).Assembly);

        Ensure(error is not null, $"API {apiVersion} should be rejected");
        Ensure(error!.Code == SharpLinkAssemblyRegistrationErrorCode.IncompatibleManifest,
            $"API {apiVersion} should use the incompatible-manifest error code");
        Ensure(error.Message.Contains(
                $"API {apiVersion}/{SharpLinkGeneratedManifestVersions.Api}",
                StringComparison.Ordinal),
            "diagnostic should carry incoming and required API versions");
        Ensure(error.Message.Contains(
                $"Protocol {manifest.ProtocolVersion}/{SharpLinkGeneratedManifestVersions.Protocol}",
                StringComparison.Ordinal),
            "diagnostic should carry incoming and required Protocol versions");
        Ensure(error.Message.Contains(manifest.GeneratorVersion, StringComparison.Ordinal),
            "diagnostic should identify the incoming Generator");
        Ensure(error.Message.Contains("Action: delete stale generated outputs", StringComparison.Ordinal) &&
               error.Message.Contains("regenerate and rebuild", StringComparison.Ordinal) &&
               error.Message.Contains("SharpLink SDK", StringComparison.Ordinal),
            "diagnostic should provide an actionable regeneration and rebuild path");
        Ensure(error.IncomingAssembly == typeof(GeneratedManifestCompatibilityTests).Assembly.FullName,
            "diagnostic should identify the incoming owner assembly");
        Ensure(error.IncomingLoadContext == SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(
                   typeof(GeneratedManifestCompatibilityTests).Assembly),
            "diagnostic should identify the incoming AssemblyLoadContext");
        Ensure(manifest.ShapeReads == 0,
            "unsupported API rejection must precede descriptor and Codec shape reads");
    }

    [Test]
    public void ValidatorShouldRejectWrongProtocolBeforeReadingManifestShape()
    {
        var manifest = new ProbeManifest(
            SharpLinkGeneratedManifestVersions.Api,
            SharpLinkGeneratedManifestVersions.Protocol + 1,
            typeof(GeneratedManifestCompatibilityTests).Assembly);

        var error = SharpLinkGeneratedManifestCompatibility.Validate(
            manifest,
            typeof(GeneratedManifestCompatibilityTests).Assembly);

        Ensure(error?.Code == SharpLinkAssemblyRegistrationErrorCode.IncompatibleManifest,
            "wrong Protocol should use the incompatible-manifest error code");
        Ensure(error!.Message.Contains(
                   $"API {SharpLinkGeneratedManifestVersions.Api}/{SharpLinkGeneratedManifestVersions.Api}",
                   StringComparison.Ordinal) &&
               error.Message.Contains(
                   $"Protocol {manifest.ProtocolVersion}/{SharpLinkGeneratedManifestVersions.Protocol}",
                   StringComparison.Ordinal) &&
               error.Message.Contains(manifest.GeneratorVersion, StringComparison.Ordinal) &&
               error.Message.Contains("regenerate and rebuild", StringComparison.Ordinal),
            "wrong-Protocol diagnostic should carry both version axes, Generator, and action");
        Ensure(error.IncomingAssembly == typeof(GeneratedManifestCompatibilityTests).Assembly.FullName &&
               error.IncomingLoadContext == SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(
                   typeof(GeneratedManifestCompatibilityTests).Assembly),
            "wrong-Protocol diagnostic should carry Assembly and ALC identities");
        Ensure(manifest.ShapeReads == 0,
            "wrong Protocol rejection must precede descriptor and Codec shape reads");
    }

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
    public void ValidatorShouldValidateShapeBeforeRejectingOwnership()
    {
        var manifest = new OwnershipProbeManifest(typeof(string).Assembly);

        var error = SharpLinkGeneratedManifestCompatibility.Validate(
            manifest,
            typeof(GeneratedManifestCompatibilityTests).Assembly);

        Ensure(error is not null, "foreign manifest owner should be rejected");
        Ensure(error!.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
            "ownership mismatch should use the invalid-manifest error code");
        Ensure(error.Message.Contains("does not match", StringComparison.Ordinal),
            "ownership diagnostic should state the mismatch");
        Ensure(manifest.ShapeReads >= 5,
            "all required manifest shape fields must be validated before ownership");
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
        Ensure(failure.Message.Contains("regenerate and rebuild", StringComparison.Ordinal) &&
               failure.Message.Contains(
                   typeof(GeneratedManifestCompatibilityTests).Assembly.FullName!,
                   StringComparison.Ordinal) &&
               failure.Message.Contains(
                   SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(
                       typeof(GeneratedManifestCompatibilityTests).Assembly),
                   StringComparison.Ordinal),
            "runtime rejection should preserve the action, Assembly, and ALC fields");
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

    private sealed class OwnershipProbeManifest(Assembly ownerAssembly) :
        ISharpLinkGeneratedAssemblyManifest
    {
        private int _shapeReads;

        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "p3-api4-test";
        public Assembly OwnerAssembly => ownerAssembly;
        public int ShapeReads => Volatile.Read(ref _shapeReads);
        public string CompileTimeDescriptor
        {
            get
            {
                Interlocked.Increment(ref _shapeReads);
                return "test";
            }
        }
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts
        {
            get
            {
                Interlocked.Increment(ref _shapeReads);
                return [];
            }
        }
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services
        {
            get
            {
                Interlocked.Increment(ref _shapeReads);
                return [];
            }
        }
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs
        {
            get
            {
                Interlocked.Increment(ref _shapeReads);
                return [];
            }
        }
        public IReadOnlyList<string> Dependencies
        {
            get
            {
                Interlocked.Increment(ref _shapeReads);
                return [];
            }
        }
    }
}
