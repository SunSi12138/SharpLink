using System.Reflection;

namespace SharpLink.Client;

internal sealed partial class ClientAssemblyRegistry
{
    // Bounded cold transaction storage. No ordinary RPC acquires this owner/counter.
    private readonly record struct DependencyPreparationPin(
        Assembly? Assembly, SharpLinkDynamicModule? Module, RpcGeneratedManifestRegistration? Registration);

    private static Assembly[] ResolveDependencyAssemblies(
        PreparedGeneratedManifest incoming, ColdMetadataPreparation preparation)
    {
        var referenced = incoming.ReferencedCodecDependencies;
        var maximum = checked(incoming.Dependencies.Count + incoming.ContractDependencies.Count + referenced.Count);
        preparation.ReserveCollection(maximum);
        var selected = new List<Assembly>(maximum);
        void Add(Assembly assembly)
        {
            if (ReferenceEquals(assembly, incoming.OwnerAssembly) || selected.Any(existing => ReferenceEquals(existing, assembly)))
                return;
            selected.Add(assembly);
        }
        using var callback = preparation.EnterCallback();
        foreach (var identity in EnumerateManifestDependencies(incoming))
        {
            // Capture already resolved this exact identity; never rebind a prepared declaration.
            var assembly = SharpLinkGeneratedDependencyBinding.ResolveDeclared(incoming, identity)
                ?? throw new ColdMetadataException(SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
                    $"Generated dependency '{identity}' does not resolve to an exact owner generation.");
            Add(assembly);
        }
        foreach (var dependency in referenced) Add(dependency.TargetType.Assembly);
        preparation.ReserveCollection(selected.Count);
        return selected.ToArray();
    }

    // Caller holds _gate. All selected assemblies are already resolved; no user callbacks here.
    private DependencyPreparationPin[] AcquireDependencyPinsLocked(
        Assembly[] dependencies, ColdMetadataPreparation preparation, SharpLinkDynamicModule? excluded = null)
    {
        preparation.ReserveCollection(dependencies.Length);
        var pins = new DependencyPreparationPin[dependencies.Length];
        try
        {
            for (var index = 0; index < dependencies.Length; index++)
            {
                var assembly = dependencies[index];
                if (!_dynamicModules.TryGetValue(assembly, out var module))
                {
                    if (_staticManifests.Any(manifest => ReferenceEquals(manifest.OwnerAssembly, assembly)))
                        continue; // Existing Domain preparation keeps static owners alive through rollback.
                    throw new ColdMetadataException(SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
                        $"Dependency '{assembly.FullName}' is not an exact registered owner.");
                }
                if (ReferenceEquals(module, excluded) || !module.TryAcquirePreparation())
                    throw new ColdMetadataException(SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                        $"Dependency '{assembly.FullName}' no longer accepts cold preparation.");
                // A successful preparation pin prevents physical retirement/MarkReleased.
                pins[index] = new DependencyPreparationPin(assembly, module, null);
                pins[index] = new DependencyPreparationPin(assembly, module, module.CodecRegistration);
            }
            return pins;
        }
        catch (Exception original)
        {
            Exception? failure = original;
            ReleaseDependencyPins(pins, ref failure);
            ThrowRetirementFailure(failure);
            throw;
        }
    }

    // Caller holds _gate; state and exact registration identity are checked at final publication.
    private bool DependencyPinsRemainCurrent(DependencyPreparationPin[] pins)
    {
        foreach (var pin in pins)
        {
            if (pin.Module is null) continue;
            if (!_dynamicModules.TryGetValue(pin.Assembly!, out var current) || !ReferenceEquals(current, pin.Module) ||
                current.State != SharpLinkDynamicModuleState.Running || !ReferenceEquals(current.CodecRegistration, pin.Registration))
                return false;
        }
        return true;
    }

    private static void ReleaseDependencyPins(DependencyPreparationPin[] pins, ref Exception? failure)
    {
        for (var index = 0; index < pins.Length; index++)
        {
            var module = pins[index].Module;
            pins[index] = default; // Clear exact metadata roots before refunding transaction storage.
            if (module is null) continue;
            try { module.ReleasePreparation(); }
            catch (Exception exception) { AddRetirementFailure(ref failure, exception); }
        }
    }
}
