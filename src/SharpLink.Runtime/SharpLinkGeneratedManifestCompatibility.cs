using System.Reflection;

namespace SharpLink.Runtime;

internal static class SharpLinkGeneratedManifestCompatibility
{
    internal static SharpLinkAssemblyRegistrationError? Validate(
        ISharpLinkGeneratedAssemblyManifest manifest)
        => Validate(manifest, expectedOwner: null);

    internal static SharpLinkAssemblyRegistrationError? Validate(
        ISharpLinkGeneratedAssemblyManifest manifest,
        Assembly? expectedOwner)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        try
        {
            var versionError = ValidateVersion(manifest, expectedOwner);
            if (versionError is not null)
                return versionError;

            var shapeError = ValidateShape(manifest, expectedOwner ?? manifest.GetType().Assembly);
            if (shapeError is not null)
                return shapeError;

            return ValidateOwnership(manifest, expectedOwner);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return Error(
                SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                $"The generated manifest could not be validated: {exception.GetType().Name}: {exception.Message}",
                expectedOwner ?? manifest.GetType().Assembly,
                "Manifest");
        }
    }

    private static SharpLinkAssemblyRegistrationError? ValidateVersion(
        ISharpLinkGeneratedAssemblyManifest manifest,
        Assembly? expectedOwner)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var apiVersion = manifest.ApiVersion;
        var protocolVersion = manifest.ProtocolVersion;
        if (apiVersion != SharpLinkGeneratedManifestVersions.Api ||
            protocolVersion != SharpLinkGeneratedManifestVersions.Protocol)
        {
            return Error(
                SharpLinkAssemblyRegistrationErrorCode.IncompatibleManifest,
                FormatVersionMismatch(
                    apiVersion,
                    protocolVersion,
                    TryGetGeneratorVersion(manifest)),
                expectedOwner ?? manifest.GetType().Assembly,
                "Manifest");
        }

        return null;
    }

    internal static void ThrowIfIncompatible(ISharpLinkGeneratedAssemblyManifest manifest)
    {
        var error = Validate(manifest);
        if (error is not null)
        {
            throw new InvalidOperationException(
                $"{error.Code}: {error.Message} " +
                $"Assembly='{error.IncomingAssembly ?? "<unknown assembly>"}', " +
                $"ALC='{error.IncomingLoadContext ?? "<unknown ALC>"}'.");
        }
    }

    internal static string FormatVersionMismatch(
        int actualApiVersion,
        int actualProtocolVersion,
        string? generatorVersion)
        => $"Manifest compatibility mismatch: " +
           $"API {actualApiVersion}/{SharpLinkGeneratedManifestVersions.Api}, " +
           $"Protocol {actualProtocolVersion}/{SharpLinkGeneratedManifestVersions.Protocol}, " +
           $"Generator '{(string.IsNullOrWhiteSpace(generatorVersion) ? "<unknown>" : generatorVersion)}'. " +
           "Action: delete stale generated outputs, then regenerate and rebuild this assembly " +
           "with the SharpLink SDK version that matches the current Runtime.";

    private static SharpLinkAssemblyRegistrationError? ValidateShape(
        ISharpLinkGeneratedAssemblyManifest manifest,
        Assembly diagnosticAssembly)
    {
        if (string.IsNullOrWhiteSpace(manifest.GeneratorVersion) ||
            string.IsNullOrWhiteSpace(manifest.CompileTimeDescriptor) ||
            manifest.Contracts is null || manifest.Services is null ||
            manifest.Codecs is null || manifest.Dependencies is null)
        {
            return Error(
                SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                "The generated manifest contains a null or empty required metadata field.",
                diagnosticAssembly,
                "Manifest");
        }

        var contractIds = new HashSet<long>();
        for (var contractIndex = 0; contractIndex < manifest.Contracts.Count; contractIndex++)
        {
            var contract = manifest.Contracts[contractIndex];
            if (contract is null || contract.ContractType is null ||
                string.IsNullOrWhiteSpace(contract.ContractName) ||
                !IsFingerprint(contract.Fingerprint) || contract.Methods is null ||
                contract.ProxyFactory is null || contract.StubFactory is null)
            {
                return Error(
                    SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                    $"Contract descriptor at index {contractIndex} is malformed.",
                    diagnosticAssembly,
                    "Contract",
                    contract?.ContractName,
                    contract?.ContractId,
                    incomingFingerprint: contract?.Fingerprint);
            }
            if (!contractIds.Add(contract.ContractId))
            {
                return Error(
                    SharpLinkAssemblyRegistrationErrorCode.ContractConflict,
                    $"Manifest contains duplicate contract ID {contract.ContractId} for '{contract.ContractName}'.",
                    diagnosticAssembly,
                    "Contract",
                    contract.ContractName,
                    contract.ContractId,
                    incomingFingerprint: contract.Fingerprint);
            }

            var methodIds = new HashSet<long>();
            for (var methodIndex = 0; methodIndex < contract.Methods.Count; methodIndex++)
            {
                var method = contract.Methods[methodIndex];
                if (method is null || string.IsNullOrWhiteSpace(method.Name) ||
                    method.RequestSchema is null || method.ResponseSchema is null ||
                    !IsFingerprint(method.Fingerprint) ||
                    method.Kind is < RpcMethodKind.Unary or > RpcMethodKind.DuplexStreaming)
                {
                    return Error(
                        SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                        $"Method descriptor at index {methodIndex} for contract '{contract.ContractName}' is malformed.",
                        diagnosticAssembly,
                        "Method",
                        contract.ContractName,
                        contract.ContractId,
                        method?.Name,
                        method?.MethodId,
                        incomingFingerprint: method?.Fingerprint);
                }
                if (!methodIds.Add(method.MethodId))
                {
                    return Error(
                        SharpLinkAssemblyRegistrationErrorCode.MethodConflict,
                        $"Contract '{contract.ContractName}' contains duplicate method ID {method.MethodId} for '{method.Name}'.",
                        diagnosticAssembly,
                        "Method",
                        contract.ContractName,
                        contract.ContractId,
                        method.Name,
                        method.MethodId,
                        incomingFingerprint: method.Fingerprint);
                }
            }
        }

        var serviceContracts = new HashSet<long>();
        for (var serviceIndex = 0; serviceIndex < manifest.Services.Count; serviceIndex++)
        {
            var service = manifest.Services[serviceIndex];
            if (service is null || service.ContractType is null || service.ImplementationType is null ||
                string.IsNullOrWhiteSpace(service.ContractName) ||
                string.IsNullOrWhiteSpace(service.ImplementationName) ||
                !IsFingerprint(service.Fingerprint) || service.Dependencies is null ||
                service.Activator is null ||
                service.Lifetime is not SharpLinkServiceLifetime.Singleton and
                    not SharpLinkServiceLifetime.Connection and
                    not SharpLinkServiceLifetime.Call)
            {
                return Error(
                    SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                    $"Service descriptor at index {serviceIndex} is malformed.",
                    diagnosticAssembly,
                    "Service",
                    service?.ContractName,
                    service?.ContractId,
                    incomingFingerprint: service?.Fingerprint);
            }
            if (!serviceContracts.Add(service.ContractId))
            {
                return Error(
                    SharpLinkAssemblyRegistrationErrorCode.ServiceConflict,
                    $"Manifest contains more than one service for contract '{service.ContractName}' ({service.ContractId}).",
                    diagnosticAssembly,
                    "Service",
                    service.ContractName,
                    service.ContractId,
                    incomingFingerprint: service.Fingerprint);
            }
            for (var dependencyIndex = 0; dependencyIndex < service.Dependencies.Count; dependencyIndex++)
            {
                if (service.Dependencies[dependencyIndex] is null)
                {
                    return Error(
                        SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                        $"Service '{service.ImplementationName}' contains a null dependency type.",
                        diagnosticAssembly,
                        "Service",
                        service.ContractName,
                        service.ContractId,
                        incomingFingerprint: service.Fingerprint);
                }
            }
        }

        var codecTypes = new HashSet<Type>();
        for (var codecIndex = 0; codecIndex < manifest.Codecs.Count; codecIndex++)
        {
            var codec = manifest.Codecs[codecIndex];
            if (codec is null || codec.TargetType is null || string.IsNullOrWhiteSpace(codec.SchemaId))
            {
                return Error(
                    SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                    $"Codec descriptor at index {codecIndex} is malformed.",
                    diagnosticAssembly,
                    "Codec");
            }
            if (!codecTypes.Add(codec.TargetType))
            {
                return Error(
                    SharpLinkAssemblyRegistrationErrorCode.CodecConflict,
                    $"Manifest contains more than one Codec for '{codec.TargetType.FullName}'.",
                    diagnosticAssembly,
                    "Codec",
                    incomingFingerprint: codec.SchemaId);
            }
        }

        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        for (var dependencyIndex = 0; dependencyIndex < manifest.Dependencies.Count; dependencyIndex++)
        {
            var dependency = manifest.Dependencies[dependencyIndex];
            if (string.IsNullOrWhiteSpace(dependency) || !dependencies.Add(dependency))
            {
                return Error(
                    SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                    $"Manifest dependency at index {dependencyIndex} is empty or duplicated.",
                    diagnosticAssembly,
                    "Dependency");
            }
        }
        return null;
    }

    private static SharpLinkAssemblyRegistrationError? ValidateOwnership(
        ISharpLinkGeneratedAssemblyManifest manifest,
        Assembly? expectedOwner)
    {
        var owner = manifest.OwnerAssembly;
        if (owner is null)
        {
            return Error(
                SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                "The generated manifest does not identify an owner assembly.",
                expectedOwner,
                "Manifest");
        }
        if (expectedOwner is not null && !ReferenceEquals(owner, expectedOwner))
        {
            return Error(
                SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                $"Manifest owner '{SharpLinkAssemblyManifestLoader.GetAssemblyIdentity(owner)}' does not match " +
                $"incoming assembly '{SharpLinkAssemblyManifestLoader.GetAssemblyIdentity(expectedOwner)}'.",
                expectedOwner,
                "Manifest");
        }

        for (var contractIndex = 0; contractIndex < manifest.Contracts.Count; contractIndex++)
        {
            var contract = manifest.Contracts[contractIndex];
            if (!ReferenceEquals(contract.ContractType.Assembly, owner))
            {
                return Error(
                    SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                    $"Contract '{contract.ContractName}' is not owned by the manifest assembly.",
                    owner,
                    "Contract",
                    contract.ContractName,
                    contract.ContractId,
                    incomingFingerprint: contract.Fingerprint);
            }
        }

        for (var serviceIndex = 0; serviceIndex < manifest.Services.Count; serviceIndex++)
        {
            var service = manifest.Services[serviceIndex];
            if (!ReferenceEquals(service.ImplementationType.Assembly, owner))
            {
                return Error(
                    SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                    $"Service '{service.ImplementationName}' is not owned by the manifest assembly.",
                    owner,
                    "Service",
                    service.ContractName,
                    service.ContractId,
                    incomingFingerprint: service.Fingerprint);
            }
        }

        return null;
    }

    private static string TryGetGeneratorVersion(ISharpLinkGeneratedAssemblyManifest manifest)
    {
        try
        {
            return string.IsNullOrWhiteSpace(manifest.GeneratorVersion)
                ? "<unknown>"
                : manifest.GeneratorVersion;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return "<unavailable>";
        }
    }

    private static bool IsFingerprint(string? value)
    {
        if (value?.Length != 64)
            return false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f') and
                not (>= 'A' and <= 'F'))
            {
                return false;
            }
        }
        return true;
    }

    private static SharpLinkAssemblyRegistrationError Error(
        SharpLinkAssemblyRegistrationErrorCode code,
        string message,
        Assembly? assembly,
        string? artifact = null,
        string? contractName = null,
        long? contractId = null,
        string? methodName = null,
        long? methodId = null,
        string? existingFingerprint = null,
        string? incomingFingerprint = null)
        => new(
            code,
            message,
            assembly is null ? null : SharpLinkAssemblyManifestLoader.GetAssemblyIdentity(assembly),
            IncomingLoadContext: assembly is null
                ? null
                : SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(assembly),
            Artifact: artifact,
            ContractName: contractName,
            ContractId: contractId,
            MethodName: methodName,
            MethodId: methodId,
            ExistingFingerprint: existingFingerprint,
            IncomingFingerprint: incomingFingerprint);
}
