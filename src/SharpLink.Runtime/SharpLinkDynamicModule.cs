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
            if (locator.ConstructorArguments.Count == 1 &&
                locator.ConstructorArguments[0].Value is Type)
            {
                return Failure(
                    SharpLinkAssemblyRegistrationErrorCode.IncompatibleManifest,
                    $"Manifest compatibility mismatch: API 3/{SharpLinkGeneratedManifestVersions.Api}, " +
                    $"Protocol 2/{SharpLinkGeneratedManifestVersions.Protocol}, " +
                    "Generator '<unavailable: legacy API 3 locator>'.",
                    assembly);
            }
            if (locator.ConstructorArguments.Count != 4 ||
                locator.ConstructorArguments[0].Value is not Type manifestType ||
                locator.ConstructorArguments[1].Value is not int apiVersion ||
                locator.ConstructorArguments[2].Value is not int protocolVersion ||
                locator.ConstructorArguments[3].Value is not string generatorVersion ||
                string.IsNullOrWhiteSpace(generatorVersion))
            {
                return Failure(
                    SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                    "The SharpLink manifest locator is not a valid self-describing API 4 locator.",
                    assembly);
            }
            if (apiVersion != SharpLinkGeneratedManifestVersions.Api ||
                protocolVersion != SharpLinkGeneratedManifestVersions.Protocol)
            {
                return Failure(
                    SharpLinkAssemblyRegistrationErrorCode.IncompatibleManifest,
                    $"Manifest compatibility mismatch: API {apiVersion}/{SharpLinkGeneratedManifestVersions.Api}, " +
                    $"Protocol {protocolVersion}/{SharpLinkGeneratedManifestVersions.Protocol}, " +
                    $"Generator '{generatorVersion}'.",
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
            var validationError = SharpLinkGeneratedManifestCompatibility.Validate(generated, assembly);
            if (validationError is not null)
                return SharpLinkAssemblyRegistrationResult.Failure(validationError);
            if (generated.ApiVersion != apiVersion ||
                generated.ProtocolVersion != protocolVersion ||
                !string.Equals(generated.GeneratorVersion, generatorVersion, StringComparison.Ordinal))
            {
                return Failure(
                    SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
                    "The materialized manifest metadata does not match its self-describing locator.",
                    assembly);
            }

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
    private readonly CancellationToken _forcedCancellationToken;
    private Assembly? _assembly;
    private ISharpLinkGeneratedAssemblyManifest? _manifest;
    private RpcGeneratedManifestRegistration? _codecRegistration;
    private int _state;

    internal SharpLinkDynamicModule(
        Assembly assembly,
        ISharpLinkGeneratedAssemblyManifest manifest,
        RpcGeneratedManifestRegistration codecRegistration)
    {
        _assembly = assembly;
        _manifest = manifest;
        _codecRegistration = codecRegistration;
        _forcedCancellationToken = _forcedCancellation.Token;
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

    internal RpcGeneratedManifestRegistration CodecRegistration
        => Volatile.Read(ref _codecRegistration) ??
           throw new ObjectDisposedException(nameof(SharpLinkDynamicModule));

    internal SharpLinkDynamicModuleState State
        => (SharpLinkDynamicModuleState)Volatile.Read(ref _state);

    internal CancellationToken ForcedCancellation => _forcedCancellationToken;

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

    internal static async Task<bool> WaitForDrainAsync(Task drainTask, TimeSpan gracefulTimeout)
        => await WaitForDrainAsync(
            drainTask,
            gracefulTimeout,
            TimeProvider.System).ConfigureAwait(false);

    internal static async Task<bool> WaitForDrainAsync(
        Task drainTask,
        TimeSpan gracefulTimeout,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(drainTask);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(gracefulTimeout, TimeSpan.Zero);
        return await SharpLinkTimer.WaitAsync(
            drainTask,
            gracefulTimeout,
            timeProvider).ConfigureAwait(false);
    }

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
        Volatile.Write(ref _codecRegistration, null);
        // A dispatch can retain a route snapshot before acquiring its module lease.
        // Keep the token source usable until that stale reader drops the module; all
        // registered callbacks are already gone when the module counters drain.
    }

    internal void Release(int stripe, bool stream)
    {
        if (stream && Interlocked.Decrement(ref _streamCounters[stripe].Value) < 0)
            throw new InvalidOperationException("Dynamic module stream counter underflowed.");
        if (Interlocked.Decrement(ref _callCounters[stripe].Value) < 0)
            throw new InvalidOperationException("Dynamic module call counter underflowed.");
        if (State is (SharpLinkDynamicModuleState.Draining or SharpLinkDynamicModuleState.DrainTimedOut) &&
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
