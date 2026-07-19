using System.Reflection;
using System.Runtime.Loader;

namespace SharpLink.Runtime;

internal enum SharpLinkDynamicModuleState : byte
{
    Running,
    Draining,
    Released,
    DrainTimedOut
}

internal static class SharpLinkAssemblyManifestLoader
{
    internal static SharpLinkAssemblyRegistrationResult TryLoad(
        Assembly? assembly,
        out ISharpLinkGeneratedAssemblyManifest? manifest)
    {
        manifest = null;
        if (assembly is null)
        {
            return Failure(
                SharpLinkAssemblyRegistrationErrorCode.InvalidArgument,
                "Assembly cannot be null.");
        }
        if (!RuntimeFeature.IsDynamicCodeSupported ||
            AppContext.TryGetSwitch("SharpLink.DisableRuntimeAssemblyRegistration", out var disabled) && disabled)
        {
            return Failure(
                SharpLinkAssemblyRegistrationErrorCode.PlatformNotSupported,
                "Runtime assembly registration is unavailable when dynamic code is disabled.",
                assembly);
        }

        try
        {
            CustomAttributeData? locator = null;
            foreach (var attribute in assembly.GetCustomAttributesData())
            {
                if (!string.Equals(
                        attribute.AttributeType.FullName,
                        typeof(SharpLinkGeneratedAssemblyManifestAttribute).FullName,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                if (locator is not null)
                {
                    return Failure(
                        SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                        "The assembly contains more than one SharpLink manifest locator.",
                        assembly);
                }
                locator = attribute;
            }

            if (locator is null)
            {
                return Failure(
                    SharpLinkAssemblyRegistrationErrorCode.MissingManifest,
                    "The assembly does not contain a source-generated SharpLink manifest locator.",
                    assembly);
            }
            if (locator.ConstructorArguments.Count != 1 ||
                locator.ConstructorArguments[0].Value is not Type manifestType)
            {
                return Failure(
                    SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                    "The SharpLink manifest locator does not contain a valid manifest type.",
                    assembly);
            }
            if (!ReferenceEquals(manifestType.Assembly, assembly))
            {
                return Failure(
                    SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                    $"Manifest type '{manifestType.FullName}' is not owned by the incoming assembly.",
                    assembly);
            }
            if (Activator.CreateInstance(manifestType) is not ISharpLinkGeneratedAssemblyManifest generated)
            {
                return Failure(
                    SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                    $"Manifest type '{manifestType.FullName}' does not implement ISharpLinkGeneratedAssemblyManifest.",
                    assembly);
            }
            if (!ReferenceEquals(generated.OwnerAssembly, assembly))
            {
                return Failure(
                    SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                    $"Manifest owner '{generated.OwnerAssembly.FullName}' does not match incoming assembly '{assembly.FullName}'.",
                    assembly);
            }
            if (generated.ApiVersion != SharpLinkGeneratedManifestVersions.Api ||
                generated.ProtocolVersion != SharpLinkGeneratedManifestVersions.Protocol)
            {
                return Failure(
                    SharpLinkAssemblyRegistrationErrorCode.IncompatibleManifest,
                    $"Manifest compatibility mismatch: API {generated.ApiVersion}/{SharpLinkGeneratedManifestVersions.Api}, " +
                    $"Protocol {generated.ProtocolVersion}/{SharpLinkGeneratedManifestVersions.Protocol}, " +
                    $"Generator '{generated.GeneratorVersion}'.",
                    assembly);
            }

            var validationError = ValidateManifest(generated, assembly);
            if (validationError is not null)
                return SharpLinkAssemblyRegistrationResult.Failure(validationError);

            manifest = generated;
            return SharpLinkAssemblyRegistrationResult.Success();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return Failure(
                SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                $"The generated SharpLink manifest could not be loaded: {exception.GetType().Name}: {exception.Message}",
                assembly);
        }
    }

    internal static string GetAssemblyIdentity(Assembly assembly)
        => assembly.FullName ?? assembly.GetName().Name ?? "<unknown assembly>";

    internal static string GetLoadContextIdentity(Assembly assembly)
    {
        var context = AssemblyLoadContext.GetLoadContext(assembly);
        if (context is null)
            return "<unknown ALC>";
        return $"{context.Name ?? "Default"} (collectible={context.IsCollectible})";
    }

    private static SharpLinkAssemblyRegistrationError? ValidateManifest(
        ISharpLinkGeneratedAssemblyManifest manifest,
        Assembly assembly)
    {
        if (string.IsNullOrWhiteSpace(manifest.GeneratorVersion) ||
            string.IsNullOrWhiteSpace(manifest.CompileTimeDescriptor) ||
            manifest.Contracts is null || manifest.Services is null ||
            manifest.Codecs is null || manifest.Dependencies is null)
        {
            return Error(
                SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                "The generated manifest contains a null or empty required metadata field.",
                assembly,
                "Manifest");
        }

        var contractIds = new HashSet<long>();
        for (var contractIndex = 0; contractIndex < manifest.Contracts.Count; contractIndex++)
        {
            var contract = manifest.Contracts[contractIndex];
            if (contract is null || contract.ContractType is null ||
                !ReferenceEquals(contract.ContractType.Assembly, assembly) ||
                string.IsNullOrWhiteSpace(contract.ContractName) ||
                !IsFingerprint(contract.Fingerprint) || contract.Methods is null ||
                contract.ProxyFactory is null || contract.StubFactory is null)
            {
                return Error(
                    SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                    $"Contract descriptor at index {contractIndex} is malformed or not owned by the manifest assembly.",
                    assembly,
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
                    assembly,
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
                        assembly,
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
                        assembly,
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
                !ReferenceEquals(service.ImplementationType.Assembly, assembly) ||
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
                    assembly,
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
                    assembly,
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
                        assembly,
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
                    assembly,
                    "Codec");
            }
            if (!codecTypes.Add(codec.TargetType))
            {
                return Error(
                    SharpLinkAssemblyRegistrationErrorCode.CodecConflict,
                    $"Manifest contains more than one Codec for '{codec.TargetType.FullName}'.",
                    assembly,
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
                    assembly,
                    "Dependency");
            }
        }
        return null;
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
                return false;
        }
        return true;
    }

    private static SharpLinkAssemblyRegistrationError Error(
        SharpLinkAssemblyRegistrationErrorCode code,
        string message,
        Assembly assembly,
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
            GetAssemblyIdentity(assembly),
            IncomingLoadContext: GetLoadContextIdentity(assembly),
            Artifact: artifact,
            ContractName: contractName,
            ContractId: contractId,
            MethodName: methodName,
            MethodId: methodId,
            ExistingFingerprint: existingFingerprint,
            IncomingFingerprint: incomingFingerprint);

    private static SharpLinkAssemblyRegistrationResult Failure(
        SharpLinkAssemblyRegistrationErrorCode code,
        string message,
        Assembly? assembly = null)
        => SharpLinkAssemblyRegistrationResult.Failure(new SharpLinkAssemblyRegistrationError(
            code,
            message,
            assembly is null ? null : GetAssemblyIdentity(assembly),
            IncomingLoadContext: assembly is null ? null : GetLoadContextIdentity(assembly)));
}

internal sealed class SharpLinkDynamicModule
{
    private readonly PaddedCounter[] _callCounters;
    private readonly PaddedCounter[] _streamCounters;
    private readonly int _stripeMask;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _forcedCancellation = new();
    private Assembly? _assembly;
    private ISharpLinkGeneratedAssemblyManifest? _manifest;
    private int _state;

    internal SharpLinkDynamicModule(Assembly assembly, ISharpLinkGeneratedAssemblyManifest manifest)
    {
        _assembly = assembly;
        _manifest = manifest;
        var stripeCount = 1;
        var desired = Math.Min(64, Math.Max(2, Environment.ProcessorCount * 2));
        while (stripeCount < desired)
            stripeCount <<= 1;
        _callCounters = new PaddedCounter[stripeCount];
        _streamCounters = new PaddedCounter[stripeCount];
        _stripeMask = stripeCount - 1;
    }

    internal Assembly Assembly => Volatile.Read(ref _assembly) ??
        throw new ObjectDisposedException(nameof(SharpLinkDynamicModule));

    internal ISharpLinkGeneratedAssemblyManifest Manifest => Volatile.Read(ref _manifest) ??
        throw new ObjectDisposedException(nameof(SharpLinkDynamicModule));

    internal SharpLinkDynamicModuleState State
        => (SharpLinkDynamicModuleState)Volatile.Read(ref _state);

    internal CancellationToken ForcedCancellation => _forcedCancellation.Token;

    internal int RemainingCalls => Sum(_callCounters);

    internal int RemainingStreams => Sum(_streamCounters);

    internal bool TryAcquire(bool stream, out SharpLinkDynamicModuleLease lease)
    {
        lease = default;
        if (State != SharpLinkDynamicModuleState.Running)
            return false;
        var stripe = Thread.GetCurrentProcessorId() & _stripeMask;
        Interlocked.Increment(ref _callCounters[stripe].Value);
        if (stream)
            Interlocked.Increment(ref _streamCounters[stripe].Value);
        if (State == SharpLinkDynamicModuleState.Running)
        {
            lease = new SharpLinkDynamicModuleLease(this, stripe, stream);
            return true;
        }

        Release(stripe, stream);
        return false;
    }

    internal bool TryBeginDraining()
    {
        var changed = Interlocked.CompareExchange(
            ref _state,
            (int)SharpLinkDynamicModuleState.Draining,
            (int)SharpLinkDynamicModuleState.Running) == (int)SharpLinkDynamicModuleState.Running;
        if ((changed || State == SharpLinkDynamicModuleState.Draining) && RemainingCalls == 0)
            _drained.TrySetResult();
        return changed;
    }

    internal Task WaitForDrainAsync() => _drained.Task;

    internal void CancelRemainingCalls()
    {
        try
        {
            _forcedCancellation.Cancel();
        }
        catch
        {
        }
    }

    internal void MarkDrainTimedOut()
        => Interlocked.CompareExchange(
            ref _state,
            (int)SharpLinkDynamicModuleState.DrainTimedOut,
            (int)SharpLinkDynamicModuleState.Draining);

    internal void MarkReleased()
    {
        Interlocked.Exchange(ref _state, (int)SharpLinkDynamicModuleState.Released);
        Volatile.Write(ref _manifest, null);
        Volatile.Write(ref _assembly, null);
        _forcedCancellation.Dispose();
    }

    internal void Release(int stripe, bool stream)
    {
        if (stream && Interlocked.Decrement(ref _streamCounters[stripe].Value) < 0)
            throw new InvalidOperationException("Dynamic module stream counter underflowed.");
        if (Interlocked.Decrement(ref _callCounters[stripe].Value) < 0)
            throw new InvalidOperationException("Dynamic module call counter underflowed.");
        if (State is SharpLinkDynamicModuleState.Draining or SharpLinkDynamicModuleState.DrainTimedOut &&
            RemainingCalls == 0)
        {
            _drained.TrySetResult();
        }
    }

    private static int Sum(PaddedCounter[] counters)
    {
        long total = 0;
        for (var index = 0; index < counters.Length; index++)
            total += Volatile.Read(ref counters[index].Value);
        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct PaddedCounter
    {
        [FieldOffset(64)]
        internal long Value;
    }
}

internal readonly struct SharpLinkDynamicModuleLease : IDisposable
{
    private readonly SharpLinkDynamicModule? _module;
    private readonly int _stripe;
    private readonly bool _stream;

    internal SharpLinkDynamicModuleLease(SharpLinkDynamicModule module, int stripe, bool stream)
    {
        _module = module;
        _stripe = stripe;
        _stream = stream;
    }

    internal bool IsAcquired => _module is not null;

    public void Dispose() => _module?.Release(_stripe, _stream);
}
