using System.Reflection;

namespace SharpLink.Client;

internal sealed partial class ClientAssemblyRegistry
{
    private RegistrationCandidate BuildRegistrationCandidate(
        ISharpLinkGeneratedAssemblyManifest incoming,
        SharpLinkDynamicModule module,
        FrozenDictionary<Type, SharpLinkClient.ClientProxyRegistration> currentProxies,
        SharpLinkDynamicModule[] currentModules,
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration>? currentCodecs,
        out SharpLinkAssemblyRegistrationError? error)
    {
        error = ValidateDependencies(incoming, currentModules);
        if (error is not null)
            return default;

        var nextProxies = new Dictionary<Type, SharpLinkClient.ClientProxyRegistration>();
        foreach (var pair in currentProxies)
            nextProxies[pair.Key] = new SharpLinkClient.ClientProxyRegistration(pair.Value.Descriptor, pair.Value.Module, pair.Value.Codecs);
        var byId = nextProxies.Values.ToDictionary(
            static registration => registration.Descriptor.ContractId,
            static registration => registration);
        foreach (var contract in incoming.Contracts)
        {
            if (byId.TryGetValue(contract.ContractId, out var existing))
            {
                error = Conflict(incoming, contract,
                    FindManifest(existing.Descriptor.ContractType.Assembly, currentModules), existing.Descriptor);
                return default;
            }
            var registration = new SharpLinkClient.ClientProxyRegistration(
                contract,
                module,
                RpcGeneratedCodecResolver.GetProvider(module.CodecRegistration, contract.ContractType));
            nextProxies.Add(contract.ContractType, registration);
            byId.Add(contract.ContractId, registration);
        }

        var nextFactories = (currentCodecs ?? _runtimeContext.CreateGeneratedCodecSnapshot())
            .ToDictionary(static pair => pair.Key, static pair => pair.Value);
        foreach (var pair in module.CodecRegistration.Codecs)
        {
            var codec = pair.Value;
            if (nextFactories.TryGetValue(pair.Key, out var existingCodec))
            {
                if (existingCodec.Factory.CodecHash != codec.Factory.CodecHash)
                {
                    error = CreateError(
                        SharpLinkAssemblyRegistrationErrorCode.CodecConflict,
                        $"Codec conflict for '{pair.Key.FullName}': existing CodecHash '{existingCodec.Factory.CodecHash}', incoming CodecHash '{codec.Factory.CodecHash}'.",
                        incoming.OwnerAssembly,
                        "Codec",
                        existingCodec.Factory.CodecHash.ToString(),
                        codec.Factory.CodecHash.ToString());
                    return default;
                }
                continue;
            }
            nextFactories.Add(pair.Key, codec);
        }
        return new RegistrationCandidate(nextProxies.ToFrozenDictionary(), nextFactories);
    }

    private IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> CreateCodecSnapshotWithout(
        SharpLinkDynamicModule removedModule)
    {
        var nextFactories = _runtimeContext.CreateGeneratedCodecSnapshot()
            .ToDictionary(static pair => pair.Key, static pair => pair.Value);
        foreach (var codec in removedModule.CodecRegistration.Codecs)
        {
            var replacement = FindReplacementCodec(codec.Key, removedModule);
            if (replacement is null)
                nextFactories.Remove(codec.Key);
            else
                nextFactories[codec.Key] = replacement;
        }
        return nextFactories;
    }

    private SharpLinkAssemblyRegistrationError? ValidateReplacementDependants(
        SharpLinkDynamicModule oldModule,
        ISharpLinkGeneratedAssemblyManifest incoming)
    {
        var oldAssembly = oldModule.Manifest.OwnerAssembly;
        var oldIdentity = oldAssembly.FullName;
        var newIdentity = incoming.OwnerAssembly.FullName;
        foreach (var candidate in _dynamicModules.Values)
        {
            if (!ReferenceEquals(candidate, oldModule) &&
                ManifestDependsOn(candidate.Manifest, oldAssembly))
            {
                return CreateError(
                    SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
                    $"Assembly '{candidate.Manifest.OwnerAssembly.FullName}' depends on '{oldIdentity}', " +
                    $"so it cannot remain registered after replacement by '{newIdentity}'.",
                    incoming.OwnerAssembly,
                    artifact: "Dependency");
            }
        }
        return null;
    }

    private static IEnumerable<string> EnumerateManifestDependencies(ISharpLinkGeneratedAssemblyManifest manifest)
    {
        foreach (var dependency in manifest.Dependencies)
            yield return dependency;
        foreach (var dependency in manifest.ContractDependencies)
            yield return dependency;
    }

    private static bool ManifestDependsOn(
        ISharpLinkGeneratedAssemblyManifest manifest,
        Assembly ownerAssembly)
        => SharpLinkGeneratedDependencyBinding.ManifestDependsOn(manifest, ownerAssembly);

    private SharpLinkAssemblyRegistrationError? ValidateDependencies(
        ISharpLinkGeneratedAssemblyManifest incoming,
        SharpLinkDynamicModule[] currentModules)
    {
        var available = new HashSet<Assembly>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < _staticManifests.Count; index++)
            available.Add(_staticManifests[index].OwnerAssembly);
        for (var index = 0; index < currentModules.Length; index++)
        {
            var module = currentModules[index];
            if (module.State == SharpLinkDynamicModuleState.Running)
                available.Add(module.Manifest.OwnerAssembly);
        }
        var self = incoming.OwnerAssembly.FullName;
        foreach (var dependency in EnumerateManifestDependencies(incoming).Distinct(StringComparer.Ordinal))
        {
            var boundAssembly = SharpLinkGeneratedDependencyBinding.Resolve(
                incoming.OwnerAssembly,
                dependency);
            if (ReferenceEquals(boundAssembly, incoming.OwnerAssembly) ||
                boundAssembly is not null && available.Contains(boundAssembly))
            {
                continue;
            }
            return CreateError(SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
                $"Generated dependency '{dependency}' must resolve through '{self}' to the exact registered and running Assembly generation before registration.",
                incoming.OwnerAssembly, "Dependency");
        }
        if (incoming is ISharpLinkReferencedCodecDependencyManifest referencedManifest)
        {
            foreach (var dependency in referencedManifest.ReferencedCodecDependencies)
            {
                var dependencyAssembly = dependency.TargetType.Assembly;
                if (ReferenceEquals(dependencyAssembly, incoming.OwnerAssembly) || available.Contains(dependencyAssembly))
                    continue;
                return CreateError(
                    SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
                    $"Referenced generated Codec dependency '{dependency.TargetType.FullName}' must be owned by the exact registered and running Assembly generation '{dependencyAssembly.FullName}' before registration.",
                    incoming.OwnerAssembly,
                    "Dependency");
            }
        }
        return null;
    }

    private IEnumerable<ISharpLinkGeneratedAssemblyManifest> EnumerateRegisteredManifests(SharpLinkDynamicModule[] modules)
    {
        for (var index = 0; index < _staticManifests.Count; index++)
            yield return _staticManifests[index];
        for (var index = 0; index < modules.Length; index++)
            yield return modules[index].Manifest;
    }

    private ISharpLinkGeneratedAssemblyManifest FindManifest(Assembly assembly, SharpLinkDynamicModule[] modules)
        => EnumerateRegisteredManifests(modules).First(manifest => ReferenceEquals(manifest.OwnerAssembly, assembly));

    private bool IsAssemblyRegistered(Assembly assembly)
        => _dynamicModules.ContainsKey(assembly) ||
           _staticManifests.Any(manifest => ReferenceEquals(manifest.OwnerAssembly, assembly));

    private Assembly[] GetDynamicAssembliesForShutdown()
    {
        SharpLinkDynamicModule[] modules;
        lock (_gate)
            modules = [.. _dynamicModules.Values];

        if (modules.Length == 0)
            return [];
        if (modules.Length == 1)
            return [modules[0].Assembly];

        var manifests = modules.Select(static module => module.Manifest).ToArray();
        var order = SharpLinkGeneratedDependencyBinding.GetDependantsFirstOrder(manifests);
        var assemblies = new Assembly[order.Length];
        for (var index = 0; index < order.Length; index++)
            assemblies[index] = modules[order[index]].Assembly;
        return assemblies;
    }

    private void EnsureNoDynamicDependants(SharpLinkDynamicModule module)
    {
        var ownerAssembly = module.Manifest.OwnerAssembly;
        var identity = ownerAssembly.FullName;
        foreach (var candidate in _dynamicModules.Values)
        {
            if (!ReferenceEquals(candidate, module) &&
                ManifestDependsOn(candidate.Manifest, ownerAssembly))
                throw new InvalidOperationException(
                    $"Assembly '{identity}' cannot be unregistered while '{candidate.Manifest.OwnerAssembly.FullName}' depends on it.");
        }
    }

    private static ValueTask<SharpLinkAssemblyUnregisterResult> WaitForUnregisterAsync(
        Task<SharpLinkAssemblyUnregisterResult> operation,
        CancellationToken cancellationToken)
        => new SharpLinkRetirementHandle<SharpLinkAssemblyUnregisterResult>(operation)
            .WaitAsync(cancellationToken);

    private static async ValueTask<SharpLinkAssemblyReplacementResult> WaitForReplacementAsync(
        Task<SharpLinkAssemblyUnregisterResult> operation,
        CancellationToken cancellationToken)
    {
        var drain = await new SharpLinkRetirementHandle<SharpLinkAssemblyUnregisterResult>(operation)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return SharpLinkAssemblyReplacementResult.Published(drain);
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowAfterAssemblyRollback(
        SharpLinkAssemblyRegistrationError? rollbackError,
        Exception? rollbackException,
        Exception cleanupException)
    {
        if (rollbackException is not null)
            throw new AggregateException(rollbackException, cleanupException);
        if (rollbackError is not null)
        {
            throw new AggregateException(
                new InvalidOperationException($"{rollbackError.Code}: {rollbackError.Message}"),
                cleanupException);
        }
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupException).Throw();
        throw new System.Diagnostics.UnreachableException();
    }

    private static SharpLinkAssemblyRegistrationResult Failure(
        SharpLinkAssemblyRegistrationErrorCode code, string message, Assembly assembly)
        => SharpLinkAssemblyRegistrationResult.Failure(CreateError(code, message, assembly));

    private static SharpLinkAssemblyRegistrationError CreateError(
        SharpLinkAssemblyRegistrationErrorCode code,
        string message,
        Assembly assembly,
        string? artifact = null,
        string? existingFingerprint = null,
        string? incomingFingerprint = null)
        => new(code, message,
            SharpLinkAssemblyManifestLoader.GetAssemblyIdentity(assembly),
            IncomingLoadContext: SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(assembly),
            Artifact: artifact,
            ExistingFingerprint: existingFingerprint,
            IncomingFingerprint: incomingFingerprint);

    private static SharpLinkAssemblyRegistrationError Conflict(
        ISharpLinkGeneratedAssemblyManifest incoming,
        SharpLinkGeneratedContractDescriptor incomingContract,
        ISharpLinkGeneratedAssemblyManifest existing,
        SharpLinkGeneratedContractDescriptor existingContract)
        => new(SharpLinkAssemblyRegistrationErrorCode.ContractConflict,
            $"Contract conflict for '{incomingContract.ContractName}' ({incomingContract.ContractId}). " +
            $"Incoming Assembly='{incoming.OwnerAssembly.FullName}', ALC='{SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(incoming.OwnerAssembly)}', Fingerprint='{incomingContract.Fingerprint}'; " +
            $"Existing Assembly='{existing.OwnerAssembly.FullName}', ALC='{SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(existing.OwnerAssembly)}', Fingerprint='{existingContract.Fingerprint}'.",
            SharpLinkAssemblyManifestLoader.GetAssemblyIdentity(incoming.OwnerAssembly),
            SharpLinkAssemblyManifestLoader.GetAssemblyIdentity(existing.OwnerAssembly),
            SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(incoming.OwnerAssembly),
            SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(existing.OwnerAssembly),
            "Contract", incomingContract.ContractName, incomingContract.ContractId,
            ExistingFingerprint: existingContract.Fingerprint,
            IncomingFingerprint: incomingContract.Fingerprint);

    private readonly record struct RegistrationCandidate(
        FrozenDictionary<Type, SharpLinkClient.ClientProxyRegistration> Proxies,
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> Codecs);
}
