using System.Reflection;

namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private static SharpLinkAssemblyRegistrationResult Failure(
        SharpLinkAssemblyRegistrationErrorCode code,
        string message,
        Assembly assembly)
        => SharpLinkAssemblyRegistrationResult.Failure(CreateError(code, message, assembly));

    private static SharpLinkAssemblyRegistrationError CreateError(
        SharpLinkAssemblyRegistrationErrorCode code,
        string message,
        Assembly assembly,
        string? artifact = null,
        string? contractName = null,
        long? contractId = null,
        string? existingFingerprint = null,
        string? incomingFingerprint = null)
        => new(
            code,
            message,
            SharpLinkAssemblyManifestLoader.GetAssemblyIdentity(assembly),
            IncomingLoadContext: SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(assembly),
            Artifact: artifact,
            ContractName: contractName,
            ContractId: contractId,
            ExistingFingerprint: existingFingerprint,
            IncomingFingerprint: incomingFingerprint);

    private static SharpLinkAssemblyRegistrationError Conflict(
        SharpLinkAssemblyRegistrationErrorCode code,
        string artifact,
        ISharpLinkGeneratedAssemblyManifest incoming,
        string contractName,
        long contractId,
        string incomingFingerprint,
        ISharpLinkGeneratedAssemblyManifest existing,
        string existingFingerprint)
        => new(
            code,
            $"{artifact} conflict for '{contractName}' ({contractId}). Incoming Assembly='{incoming.OwnerAssembly.FullName}', " +
            $"ALC='{SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(incoming.OwnerAssembly)}', Fingerprint='{incomingFingerprint}'; " +
            $"Existing Assembly='{existing.OwnerAssembly.FullName}', " +
            $"ALC='{SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(existing.OwnerAssembly)}', Fingerprint='{existingFingerprint}'.",
            SharpLinkAssemblyManifestLoader.GetAssemblyIdentity(incoming.OwnerAssembly),
            SharpLinkAssemblyManifestLoader.GetAssemblyIdentity(existing.OwnerAssembly),
            SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(incoming.OwnerAssembly),
            SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(existing.OwnerAssembly),
            artifact,
            contractName,
            contractId,
            ExistingFingerprint: existingFingerprint,
            IncomingFingerprint: incomingFingerprint);

}
