using System;
using System.Linq;
using SharpLink.Abstractions;

namespace SharpLink.StaticCodecCoreEvidence;

internal static class Program
{
    public static int Main()
    {
        var owner = typeof(IStaticCodecCoreEvidenceRpc).Assembly;
        var manifest = SharpLinkGeneratedAssemblyCatalog.CreateSnapshot()
            .SingleOrDefault(candidate => ReferenceEquals(candidate.OwnerAssembly, owner));

        if (manifest is null)
            throw new InvalidOperationException("Static Codec Core evidence manifest was not registered.");

        if (manifest.ApiVersion != SharpLinkGeneratedManifestVersions.Api ||
            manifest.ProtocolVersion != SharpLinkGeneratedManifestVersions.Protocol)
        {
            throw new InvalidOperationException(
                $"Generated manifest version mismatch: API {manifest.ApiVersion}, Protocol {manifest.ProtocolVersion}.");
        }

        Console.WriteLine(
            $"SharpLink static Codec Core evidence: API {manifest.ApiVersion}, Protocol {manifest.ProtocolVersion}, ABI {SharpLinkGeneratedManifestVersions.AbiIdentity}");
        return 0;
    }
}
