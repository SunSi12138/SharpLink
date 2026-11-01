using System.Reflection;

namespace SharpLink.Client;

internal sealed partial class ClientAssemblyRegistry
{
    private RegistrationCandidate BuildRegistrationCandidate(
        ISharpLinkGeneratedAssemblyManifest incoming,
        SharpLinkDynamicModule module,
        FrozenDictionary<Type, SharpLinkClient.ClientProxyRegistration> currentProxies,
        ClientProxySnapshot sourceOwners,
        SharpLinkDynamicModule[] currentModules,
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> currentCodecs,
        ColdMetadataPreparation preparation,
        out SharpLinkAssemblyRegistrationError? error)
    {
        using (preparation.EnterCallback())
            error = ValidateDependencies(incoming, currentModules, preparation);
        if (error is not null)
            return default;

        var proxyCount = checked(currentProxies.Count + incoming.Contracts.Count);
        for (var copy = 0; copy < 3; copy++) preparation.ReserveCollection(proxyCount);
        var nextProxies = new Dictionary<Type, SharpLinkClient.ClientProxyRegistration>(proxyCount);
        foreach (var pair in currentProxies)
            nextProxies[pair.Key] = pair.Value;
        var byId = new Dictionary<long, SharpLinkClient.ClientProxyRegistration>(proxyCount);
        foreach (var registration in nextProxies.Values) byId.Add(registration.Descriptor.ContractId, registration);
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
                RpcGeneratedCodecResolver.GetProvider(module.CodecRegistration, contract.ContractType))
            {
                LocalControl = CreateLocalControl(contract.ContractId, incoming.RpcAssemblyHash)
            };
            nextProxies.Add(contract.ContractType, registration);
            byId.Add(contract.ContractId, registration);
        }

        var codecSource = currentCodecs;
        preparation.ReserveCollection(checked(codecSource.Count + module.CodecRegistration.Codecs.Count));
        var nextFactories = new Dictionary<Type, RpcGeneratedCodecRegistration>(
            checked(codecSource.Count + module.CodecRegistration.Codecs.Count));
        foreach (var pair in codecSource) nextFactories.Add(pair.Key, pair.Value);
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
        return new RegistrationCandidate(ClientProxySnapshot.Create(nextProxies.ToFrozenDictionary(), preparation, sourceOwners, (PreparedGeneratedManifest)incoming), nextFactories);
    }

    private IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> CreateCodecSnapshotWithout(
        SharpLinkDynamicModule removedModule,
        SharpLinkRuntimeContext.CapturedGeneratedCodecs codecSource,
        ColdMetadataPreparation preparation)
    {
        var source = codecSource.Registrations;
        preparation.ReserveCollection(source.Count);
        var nextFactories = new Dictionary<Type, RpcGeneratedCodecRegistration>(source.Count);
        foreach (var pair in source) nextFactories.Add(pair.Key, pair.Value);
        foreach (var codec in removedModule.CodecRegistration.Codecs)
        {
            var replacement = FindReplacementCodec(codec.Key, removedModule, codecSource);
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
        SharpLinkDynamicModule[] currentModules,
        ColdMetadataPreparation? preparation = null)
    {
        var availableCount = checked(_staticManifests.Count + currentModules.Length);
        preparation?.ReserveCollection(availableCount);
        preparation?.ReserveCollection(checked(incoming.Dependencies.Count + incoming.ContractDependencies.Count));
        var available = new HashSet<Assembly>(availableCount, ReferenceEqualityComparer.Instance);
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
            var boundAssembly = SharpLinkGeneratedDependencyBinding.ResolveDeclared(
                incoming,
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
        => _dynamicModules.ContainsKey(assembly) || _unregisterOperations.ContainsKey(assembly) ||
           _staticManifests.Any(manifest => ReferenceEquals(manifest.OwnerAssembly, assembly));

    private KeyValuePair<Assembly, SharpLinkDynamicModule>[] GetDynamicModulesForShutdown(
        out (Task<SharpLinkAssemblyUnregisterResult> Task, SharpLinkDynamicModule Module)[] terminal)
    {
        terminal = [];
        KeyValuePair<Assembly, SharpLinkDynamicModule>[] modules = [];
        ISharpLinkGeneratedAssemblyManifest[] manifests = [];
        IDisposable?[] leases = [];
        KeyValuePair<Assembly, SharpLinkDynamicModule>[] ordered = [];
        Exception? failure = null;
        try
        {
            lock (_gate)
            {
                // One exact cut includes both metadata and removed-but-unpublished outcomes.
                foreach (var pair in _unregisterOperations)
                    if (_dynamicModules.TryGetValue(pair.Key, out var active) && !ReferenceEquals(active, pair.Value.Module))
                        throw new InvalidOperationException("Retirement operation and active module generation disagree.");
                terminal = _unregisterOperations.Where(pair => !_dynamicModules.ContainsKey(pair.Key))
                    .Select(static pair => pair.Value).ToArray();
                modules = _dynamicModules.ToArray();
                manifests = new ISharpLinkGeneratedAssemblyManifest[modules.Length];
                leases = new IDisposable?[modules.Length];
                for (var index = 0; index < modules.Length; index++)
                {
                    // Module properties refer to the already-frozen prepared manifest, not user getters.
                    leases[index] = modules[index].Value.CodecRegistration.MetadataLifetime.Retain();
                    manifests[index] = modules[index].Value.Manifest;
                }
            }
            // Dependency binding can invoke ALC resolution. No host registry gate is held here.
            int[] order;
            using (_runtimeContext.EnterOwnedMetadataCallback())
                order = SharpLinkGeneratedDependencyBinding.GetDependantsFirstOrder(manifests);
            ordered = new KeyValuePair<Assembly, SharpLinkDynamicModule>[order.Length];
            for (var index = 0; index < order.Length; index++) ordered[index] = modules[order[index]];
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            Array.Clear(manifests);
            Array.Clear(modules);
            foreach (var lease in leases) AttemptRetirementCleanup(lease, ref failure);
            Array.Clear(leases);
        }
        if (failure is not null)
        {
            Array.Clear(ordered);
            Array.Clear(terminal);
            ThrowRetirementFailure(failure);
        }
        return ordered;
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
        ClientProxySnapshot Proxies,
        IReadOnlyDictionary<Type, RpcGeneratedCodecRegistration> Codecs);
}
