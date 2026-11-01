using System.Reflection;

namespace SharpLink.Runtime;

// The only framework metadata input after capture. Original callbacks are retained solely for
// factory execution/identity; none of the table getters below delegate to the input manifest.
internal sealed class PreparedGeneratedManifest : ISharpLinkGeneratedAssemblyManifest, ISharpLinkReferencedCodecDependencyManifest
{
    private Assembly?[] _dependencyAssemblies = [];
    private Assembly?[] _contractDependencyAssemblies = [];
    private ColdMetadataScope[] _contractScopes = [];
    private ColdMetadataReservation[] _contractMethodReservations = [];
    private PreparedGeneratedManifest(ISharpLinkGeneratedAssemblyManifest original,
        ColdMetadataPreparation preparation, ColdMetadataScope scope)
    { OriginalManifest = original; Preparation = preparation; RegistrationScope = scope; }
    internal ISharpLinkGeneratedAssemblyManifest OriginalManifest { get; }
    internal ColdMetadataPreparation Preparation { get; }
    internal ColdMetadataScope RegistrationScope { get; }
    internal ColdMetadataScope Scope => RegistrationScope;
    public int ApiVersion { get; private set; }
    public int ProtocolVersion { get; private set; }
    public string GeneratorVersion { get; private set; } = "";
    public Assembly OwnerAssembly { get; private set; } = null!;
    public RpcHash128 RpcAssemblyHash { get; private set; }
    public string CompileTimeDescriptor { get; private set; } = "";
    public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; private set; } = [];
    public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services { get; private set; } = [];
    public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; private set; } = [];
    public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs { get; private set; } = [];
    public IReadOnlyList<string> Dependencies { get; private set; } = [];
    public IReadOnlyList<string> ContractDependencies { get; private set; } = [];
    public IReadOnlyList<SharpLinkReferencedCodecDependency> ReferencedCodecDependencies { get; private set; } = [];

    internal ColdMetadataScope GetContractScope(long contractId)
    {
        for (var index = 0; index < Contracts.Count; index++)
            if (Contracts[index].ContractId == contractId) return _contractScopes[index];
        throw new InvalidOperationException("The contract is not in this exact prepared registration.");
    }

    // Borrowed exact method-domain token. A local facts table retains this token;
    // it must not dispose the metadata preparation's original ownership.
    internal ColdMetadataReservation GetContractMethodReservation(long contractId)
    {
        for (var index = 0; index < Contracts.Count; index++)
            if (Contracts[index].ContractId == contractId) return _contractMethodReservations[index];
        throw new InvalidOperationException("The contract is not in this exact prepared registration.");
    }

    internal static PreparedGeneratedManifest Capture(ISharpLinkGeneratedAssemblyManifest source,
        ColdMetadataPreparation preparation, Assembly? expectedOwner = null, PreparedManifestHeader? capturedHeader = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(preparation);
        if (source is PreparedGeneratedManifest already)
        {
            if (expectedOwner is not null && !ReferenceEquals(expectedOwner, already.OwnerAssembly))
                throw ColdMetadataException.Invalid("Prepared manifest owner does not match the incoming assembly.");
            preparation.Share(already.Preparation);
            return already;
        }
        using var callback = preparation.EnterCallback();
        var scope = preparation.CreateRegistrationScope();
        preparation.ReserveCollection(0, scope); // Manifest control/header before scalar metadata callbacks.
        var result = new PreparedGeneratedManifest(source, preparation, scope)
        {
            ApiVersion = capturedHeader?.ApiVersion ?? source.ApiVersion,
            ProtocolVersion = capturedHeader?.ProtocolVersion ?? source.ProtocolVersion,
            GeneratorVersion = capturedHeader?.GeneratorVersion ?? source.GeneratorVersion
        };
        if (result.ApiVersion != SharpLinkGeneratedManifestVersions.Api || result.ProtocolVersion != SharpLinkGeneratedManifestVersions.Protocol)
            throw new ColdMetadataException(SharpLinkAssemblyRegistrationErrorCode.IncompatibleManifest,
                SharpLinkGeneratedManifestCompatibility.FormatVersionMismatch(result.ApiVersion, result.ProtocolVersion, result.GeneratorVersion));
        result.OwnerAssembly = source.OwnerAssembly;
        var versionError = SharpLinkGeneratedManifestCompatibility.ValidatePreparedVersion(result, expectedOwner);
        if (versionError is not null) throw new ColdMetadataException(versionError.Code, versionError.Message);
        if (string.IsNullOrWhiteSpace(result.GeneratorVersion))
            throw ColdMetadataException.Invalid("The generated manifest contains a null or empty required metadata field.");
        result.CompileTimeDescriptor = source.CompileTimeDescriptor;
        if (string.IsNullOrWhiteSpace(result.CompileTimeDescriptor))
            throw ColdMetadataException.Invalid("The generated manifest contains a null or empty required metadata field.");
        result.RpcAssemblyHash = source.RpcAssemblyHash;

        var contracts = source.Contracts ?? throw ColdMetadataException.Invalid("Manifest Contract table is null.");
        var contractCount = ColdMetadataCollections.Count(contracts);
        preparation.ReserveCollection(contractCount, scope);
        result._contractScopes = new ColdMetadataScope[contractCount];
        preparation.ReserveCollection(contractCount, scope);
        result._contractMethodReservations = new ColdMetadataReservation[contractCount];
        var contractIndex = 0;
        result.Contracts = ColdMetadataCollections.Copy(contracts, preparation, scope, contract =>
        {
            var contractScope = scope.CreateContractScope();
            var index = contractIndex++;
            result._contractScopes[index] = contractScope;
            var methods = contract.Methods ?? throw ColdMetadataException.Invalid("Contract Method table is null.");
            var methodCount = ColdMetadataCollections.Count(methods);
            result._contractMethodReservations[index] = preparation.ReserveMethods(methodCount, contractScope);
            return contract with { Methods = ColdMetadataCollections.Copy(methods, preparation, contractScope, expectedCount: methodCount) };
        }, expectedCount: contractCount);
        var services = source.Services ?? throw ColdMetadataException.Invalid("Manifest Service table is null.");
        result.Services = ColdMetadataCollections.Copy(services, preparation, scope, service =>
            service with { Dependencies = ColdMetadataCollections.Copy(service.Dependencies, preparation, scope) });
        var codecs = source.Codecs ?? throw ColdMetadataException.Invalid("Manifest Codec table is null.");
        result.Codecs = ColdMetadataCollections.Copy(codecs, preparation, scope, PreparedCodecFactory.Capture);
        var contractCodecs = source.ContractCodecs ?? throw ColdMetadataException.Invalid("Manifest Contract Codec table is null.");
        result.ContractCodecs = ColdMetadataCollections.Copy(contractCodecs, preparation, scope, PreparedCodecFactory.Capture);
        var dependencies = source.Dependencies ?? throw ColdMetadataException.Invalid("Manifest dependency table is null.");
        result.Dependencies = ColdMetadataCollections.Copy(dependencies, preparation, scope);
        var contractDependencies = source.ContractDependencies ?? throw ColdMetadataException.Invalid("Manifest Contract dependency table is null.");
        result.ContractDependencies = ColdMetadataCollections.Copy(contractDependencies, preparation, scope);
        if (source is ISharpLinkReferencedCodecDependencyManifest referenced)
        {
            var referencedSource = referenced.ReferencedCodecDependencies;
            result.ReferencedCodecDependencies = ColdMetadataCollections.Copy(referencedSource, preparation, scope);
            if (!ReferenceEquals(referenced.ReferencedCodecDependencies, referencedSource))
                throw ColdMetadataException.Invalid("Referenced Codec dependency table identity changed during capture.");
        }
        else
            result.ReferencedCodecDependencies = ColdMetadataCollections.Copy(Array.Empty<SharpLinkReferencedCodecDependency>(), preparation, scope);

        if (!ReferenceEquals(source.Contracts, contracts) || !ReferenceEquals(source.Services, services) ||
            !ReferenceEquals(source.Codecs, codecs) || !ReferenceEquals(source.ContractCodecs, contractCodecs) ||
            !ReferenceEquals(source.Dependencies, dependencies) || !ReferenceEquals(source.ContractDependencies, contractDependencies))
            throw ColdMetadataException.Invalid("Manifest table identity changed during capture.");
        var error = SharpLinkGeneratedManifestCompatibility.ValidatePrepared(result, expectedOwner);
        if (error is not null) throw new ColdMetadataException(error.Code, error.Message);
        // Resolve once while Capture's callback scope is active and before any host gate.
        // Null is also a binding result: later registration must not silently retry an ALC lookup.
        result._dependencyAssemblies = result.CaptureBindings(result.Dependencies, []);
        result._contractDependencyAssemblies = result.CaptureBindings(result.ContractDependencies,
            result._dependencyAssemblies);
        return result;
    }

    private Assembly?[] CaptureBindings(IReadOnlyList<string> identities, Assembly?[] prior)
    {
        if (identities.Count == 0) return [];
        var reservation = Preparation.ReserveCollection(identities.Count, RegistrationScope);
        Assembly?[]? bindings = null;
        try
        {
            bindings = new Assembly?[identities.Count];
            reservation.OwnDeclaredDependencyBindings(bindings);
            for (var index = 0; index < identities.Count; index++)
            {
                var found = false;
                for (var previous = 0; previous < prior.Length; previous++)
                    if (string.Equals(Dependencies[previous], identities[index], StringComparison.Ordinal))
                    { bindings[index] = prior[previous]; found = true; break; }
                for (var previous = 0; !found && previous < index; previous++)
                    if (string.Equals(identities[previous], identities[index], StringComparison.Ordinal))
                    { bindings[index] = bindings[previous]; found = true; break; }
                if (!found)
                    bindings[index] = SharpLinkGeneratedDependencyBinding.Resolve(OwnerAssembly, identities[index]);
            }
            return bindings;
        }
        catch
        {
            // The array never escaped. Clear even if attaching it failed, then refund independently.
            if (bindings is not null) Array.Clear(bindings);
            reservation.Dispose();
            throw;
        }
    }

    internal Assembly? ResolveDeclaredDependency(string identity)
    {
        for (var index = 0; index < Dependencies.Count; index++)
            if (string.Equals(Dependencies[index], identity, StringComparison.Ordinal))
                return _dependencyAssemblies[index];
        for (var index = 0; index < ContractDependencies.Count; index++)
            if (string.Equals(ContractDependencies[index], identity, StringComparison.Ordinal))
                return _contractDependencyAssemblies[index];
        return null;
    }

    internal bool HasDeclaredDependency(Assembly assembly)
    {
        foreach (var dependency in _dependencyAssemblies)
            if (ReferenceEquals(dependency, assembly)) return true;
        foreach (var dependency in _contractDependencyAssemblies)
            if (ReferenceEquals(dependency, assembly)) return true;
        foreach (var dependency in ReferencedCodecDependencies)
            if (ReferenceEquals(dependency.TargetType.Assembly, assembly)) return true;
        return false;
    }

    internal PreparedGeneratedManifest ForDependencyOnly(ColdMetadataPreparation preparation)
    {
        preparation.Share(Preparation);
        preparation.ReserveCollection(0, RegistrationScope);
        return new PreparedGeneratedManifest(OriginalManifest, preparation, RegistrationScope)
        {
            ApiVersion = ApiVersion, ProtocolVersion = ProtocolVersion, GeneratorVersion = GeneratorVersion,
            OwnerAssembly = OwnerAssembly, RpcAssemblyHash = RpcAssemblyHash, CompileTimeDescriptor = CompileTimeDescriptor,
            Codecs = Codecs, Dependencies = Dependencies,
            // The dependency-only plan deliberately drops contract/referenced-codec declarations.
            // Share retains the original reservation until both preparations release it.
            _dependencyAssemblies = _dependencyAssemblies
        };
    }
}

internal static class ColdMetadataCollections
{
    internal static int Count<T>(IReadOnlyList<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var count = source.Count;
        if (count < 0) throw ColdMetadataException.Invalid("Metadata collection count is negative.");
        return count;
    }
    internal static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> source, ColdMetadataPreparation preparation,
        ColdMetadataScope? scope = null, Func<T, T>? transform = null, int? expectedCount = null) where T : class
    {
        var count = Count(source);
        if (expectedCount is { } expected && count != expected)
            throw ColdMetadataException.Invalid("Metadata count changed before capture.");
        preparation.ReserveCollection(count, scope);
        var output = new T[count];
        var temporary = transform is null ? null : preparation.ReserveCollection(count, scope);
        T[]? originals = null;
        try
        {
            originals = transform is null ? output : new T[count];
            var index = 0;
            foreach (var item in source)
            {
                if (index >= count || item is null || !ReferenceEquals(item, source[index]))
                    throw ColdMetadataException.Invalid("Metadata enumeration and indexed identity disagree.");
                originals[index++] = item;
            }
            if (index != count || source.Count != count)
                throw ColdMetadataException.Invalid("Metadata count changed during capture.");
            if (transform is not null)
                for (index = 0; index < count; index++) output[index] = transform(originals[index]);
            for (index = 0; index < count; index++)
                if (!ReferenceEquals(source[index], originals[index]))
                    throw ColdMetadataException.Invalid("Metadata indexed identity changed during capture.");
            if (source.Count != count) throw ColdMetadataException.Invalid("Metadata count changed during capture.");
            return Array.AsReadOnly(output);
        }
        finally
        {
            if (transform is not null && originals is not null) Array.Clear(originals);
            temporary?.Dispose();
        }
    }

}

internal class PreparedCodecFactory : IRpcGeneratedCodecFactory
{
    protected PreparedCodecFactory(IRpcGeneratedCodecFactory source)
    {
        Original = source;
        TargetType = source.TargetType;
        CodecHash = source.CodecHash;
        AdapterId = source.AdapterId;
        Adapter = source.Adapter;
        CapturedAdapterId = Adapter?.AdapterId;
    }
    internal IRpcGeneratedCodecFactory Original { get; }
    internal string? CapturedAdapterId { get; }
    public Type TargetType { get; }
    public RpcHash128 CodecHash { get; }
    public string? AdapterId { get; }
    public IRpcCodecAdapter? Adapter { get; }
    public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? scope) => Original.Create(provider, scope);
    public bool IsCompatibleCodec(IRpcCodec codec) => Original.IsCompatibleCodec(codec);
    internal static IRpcGeneratedCodecFactory Capture(IRpcGeneratedCodecFactory source)
        => source is IRpcGeneratedUnsafeBlitCodecFactory unsafeFactory
            ? new PreparedUnsafeBlitFactory(unsafeFactory) : new PreparedCodecFactory(source);
    private sealed class PreparedUnsafeBlitFactory(IRpcGeneratedUnsafeBlitCodecFactory source)
        : PreparedCodecFactory(source), IRpcGeneratedUnsafeBlitCodecFactory
    { public SharpLinkGeneratedUnsafeBlitRequirement Requirement { get; } = source.Requirement; }
}

internal readonly record struct PreparedManifestHeader(int ApiVersion, int ProtocolVersion, string GeneratorVersion);
