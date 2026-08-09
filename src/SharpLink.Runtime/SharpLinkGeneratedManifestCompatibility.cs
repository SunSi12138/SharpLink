using System.Reflection;

namespace SharpLink.Runtime;

internal static class SharpLinkGeneratedManifestCompatibility
{
    internal static SharpLinkAssemblyRegistrationError? Validate(
        ISharpLinkGeneratedAssemblyManifest manifest)
    {
        var compatibilityError = ValidateCompatibility(manifest, expectedOwner: null, out var owner);
        return compatibilityError ?? ValidateShape(manifest, owner!);
    }

    internal static SharpLinkAssemblyRegistrationError? Validate(
        ISharpLinkGeneratedAssemblyManifest manifest,
        Assembly? expectedOwner)
    {
        var compatibilityError = ValidateCompatibility(manifest, expectedOwner, out var owner);
        return compatibilityError ?? ValidateShape(manifest, owner!);
    }

    internal static SharpLinkAssemblyRegistrationError? ValidateCompatibility(
        ISharpLinkGeneratedAssemblyManifest manifest)
        => ValidateCompatibility(manifest, expectedOwner: null, out _);

    private static SharpLinkAssemblyRegistrationError? ValidateCompatibility(
        ISharpLinkGeneratedAssemblyManifest manifest,
        Assembly? expectedOwner,
        out Assembly? owner)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        owner = null;
        try
        {
            var apiVersion = manifest.ApiVersion;
            var protocolVersion = manifest.ProtocolVersion;
            if (apiVersion != SharpLinkGeneratedManifestVersions.Api ||
                protocolVersion != SharpLinkGeneratedManifestVersions.Protocol)
            {
                var diagnosticAssembly = expectedOwner ?? TryGetOwner(manifest);
                return Error(
                    SharpLinkAssemblyRegistrationErrorCode.IncompatibleManifest,
                    $"Manifest compatibility mismatch: API {apiVersion}/{SharpLinkGeneratedManifestVersions.Api}, " +
                    $"Protocol {protocolVersion}/{SharpLinkGeneratedManifestVersions.Protocol}, " +
                    $"Generator '{TryGetGeneratorVersion(manifest)}'.",
                    diagnosticAssembly,
                    "Manifest");
            }

            owner = manifest.OwnerAssembly;
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

            return null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return Error(
                SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                $"The generated manifest could not be validated: {exception.GetType().Name}: {exception.Message}",
                expectedOwner,
                "Manifest");
        }
    }

    internal static void ThrowIfIncompatible(ISharpLinkGeneratedAssemblyManifest manifest)
    {
        var error = ValidateCompatibility(manifest);
        if (error is not null)
            throw new InvalidOperationException($"{error.Code}: {error.Message}");
    }

    private static SharpLinkAssemblyRegistrationError? ValidateShape(
        ISharpLinkGeneratedAssemblyManifest manifest,
        Assembly owner)
    {
        if (string.IsNullOrWhiteSpace(manifest.GeneratorVersion) ||
            string.IsNullOrWhiteSpace(manifest.CompileTimeDescriptor) ||
            manifest.Contracts is null || manifest.Services is null ||
            manifest.Codecs is null || manifest.Dependencies is null)
        {
            return Error(
                SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                "The generated manifest contains a null or empty required metadata field.",
                owner,
                "Manifest");
        }

        var contractIds = new HashSet<long>();
        for (var contractIndex = 0; contractIndex < manifest.Contracts.Count; contractIndex++)
        {
            var contract = manifest.Contracts[contractIndex];
            if (contract is null || contract.ContractType is null ||
                !ReferenceEquals(contract.ContractType.Assembly, owner) ||
                string.IsNullOrWhiteSpace(contract.ContractName) ||
                !IsFingerprint(contract.Fingerprint) || contract.Methods is null ||
                contract.ProxyFactory is null || contract.StubFactory is null)
            {
                return Error(
                    SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                    $"Contract descriptor at index {contractIndex} is malformed or not owned by the manifest assembly.",
                    owner,
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
                    owner,
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
                        owner,
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
                        owner,
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
                !ReferenceEquals(service.ImplementationType.Assembly, owner) ||
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
                    $"Service descriptor at index {serviceIndex} is malformed or not owned by the manifest assembly.",
                    owner,
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
                    owner,
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
                        owner,
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
                    owner,
                    "Codec");
            }
            if (!codecTypes.Add(codec.TargetType))
            {
                return Error(
                    SharpLinkAssemblyRegistrationErrorCode.CodecConflict,
                    $"Manifest contains more than one Codec for '{codec.TargetType.FullName}'.",
                    owner,
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
                    owner,
                    "Dependency");
            }
        }
        return null;
    }

    private static Assembly? TryGetOwner(ISharpLinkGeneratedAssemblyManifest manifest)
    {
        try
        {
            return manifest.OwnerAssembly;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return null;
        }
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
