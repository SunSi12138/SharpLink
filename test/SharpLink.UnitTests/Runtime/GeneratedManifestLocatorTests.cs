using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public sealed class GeneratedManifestLocatorTests
{
    private const string CurrentGeneratorVersion = "phase17-current-generator";
    private static int _fixtureId;

    [Test]
    public async Task CurrentSelfDescribingLocatorShouldLoadOneValidManifestAndReleaseItsLoadContext()
    {
        var loadContext = LoadCurrentFixtureAndRelease();

        for (var attempt = 0; attempt < 20 && loadContext.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(20);
        }

        Ensure(!loadContext.IsAlive,
            "current manifest's collectible load context should be released after fixture disposal");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadCurrentFixtureAndRelease()
    {
        using var fixture = CreateFixture();
        var loadContext = fixture.CreateLoadContextWeakReference();
        var result = SharpLinkAssemblyManifestLoader.TryLoad(fixture.Assembly, out var manifest);

        Ensure(result.Succeeded && result.Error is null,
            $"current locator should load successfully: {result.Error}");
        Ensure(manifest is not null && manifest.GetType().Assembly == fixture.Assembly,
            "loader should materialize the incoming assembly's locator-owned manifest type");
        Ensure(fixture.State.ConstructorCalls == 1,
            "current manifest should be constructed exactly once");
        Ensure(fixture.State.ShapeReads >= 5,
            "current manifest should validate every required shape collection");

        return loadContext;
    }

    [Test]
    [Arguments(3, 2)]
    [Arguments(5, 2)]
    [Arguments(4, 3)]
    public void UnsupportedLocatorVersionShouldRejectBeforeManifestConstruction(
        int locatorApiVersion,
        int locatorProtocolVersion)
    {
        using var fixture = CreateFixture(
            locatorApiVersion: locatorApiVersion,
            locatorProtocolVersion: locatorProtocolVersion);

        var result = SharpLinkAssemblyManifestLoader.TryLoad(fixture.Assembly, out var manifest);

        AssertVersionRejection(
            result,
            fixture.Assembly,
            locatorApiVersion,
            locatorProtocolVersion,
            CurrentGeneratorVersion);
        Ensure(manifest is null, "incompatible locator should publish no manifest");
        Ensure(fixture.State.ConstructorCalls == 0,
            "incompatible locator must be rejected before Activator runs");
        Ensure(fixture.State.ShapeReads == 0,
            "incompatible locator must be rejected before shape validation");
    }

    [Test]
    [Arguments(3, 2, CurrentGeneratorVersion)]
    [Arguments(4, 3, CurrentGeneratorVersion)]
    [Arguments(4, 2, "phase17-other-generator")]
    public void MaterializedMetadataMismatchShouldBeInvalidBeforeShapeValidation(
        int manifestApiVersion,
        int manifestProtocolVersion,
        string manifestGeneratorVersion)
    {
        using var fixture = CreateFixture(
            manifestApiVersion: manifestApiVersion,
            manifestProtocolVersion: manifestProtocolVersion,
            manifestGeneratorVersion: manifestGeneratorVersion);

        var result = SharpLinkAssemblyManifestLoader.TryLoad(fixture.Assembly, out var manifest);

        Ensure(!result.Succeeded && manifest is null,
            "locator/materialized metadata mismatch should publish no manifest");
        Ensure(result.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
            $"locator/materialized metadata mismatch should be invalid: {result.Error}");
        Ensure(result.Error!.Message.Contains(
                "materialized manifest metadata does not match its self-describing locator",
                StringComparison.Ordinal),
            "metadata mismatch diagnostic should identify the locator consistency failure");
        AssertOwnerFields(result.Error, fixture.Assembly);
        Ensure(fixture.State.ConstructorCalls == 1,
            "metadata consistency requires exactly one manifest construction");
        Ensure(fixture.State.ShapeReads == 0,
            "metadata mismatch must be classified before shape validation");
    }

    [Test]
    public void CurrentLocatorWithMalformedManifestShapeShouldBeInvalidAfterConstruction()
    {
        using var fixture = CreateFixture(malformedShape: true);

        var result = SharpLinkAssemblyManifestLoader.TryLoad(fixture.Assembly, out var manifest);

        Ensure(!result.Succeeded && manifest is null,
            "malformed current manifest should not be published");
        Ensure(result.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
            $"malformed current manifest should be invalid: {result.Error}");
        Ensure(result.Error!.Message.Contains("null or empty required metadata field", StringComparison.Ordinal),
            "malformed shape diagnostic should preserve the semantic validation cause");
        AssertOwnerFields(result.Error, fixture.Assembly);
        Ensure(fixture.State.ConstructorCalls == 1,
            "malformed current manifest should be constructed exactly once");
        Ensure(fixture.State.ShapeReads == 1,
            "empty compile-time descriptor should stop shape validation at the first read");
    }

    [Test]
    public void MissingLocatorShouldKeepMissingManifestContractWithoutConstruction()
    {
        using var fixture = CreateFixture(includeLocator: false);

        var result = SharpLinkAssemblyManifestLoader.TryLoad(fixture.Assembly, out var manifest);

        Ensure(!result.Succeeded && manifest is null,
            "missing locator should publish no manifest");
        Ensure(result.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.MissingManifest,
            $"missing locator should keep the missing-manifest error code: {result.Error}");
        Ensure(result.Error!.Message.Contains("does not contain", StringComparison.Ordinal),
            "missing-locator diagnostic contract should remain stable");
        AssertOwnerFields(result.Error, fixture.Assembly);
        Ensure(fixture.State.ConstructorCalls == 0 && fixture.State.ShapeReads == 0,
            "missing locator cannot construct or inspect a manifest");
    }

    [Test]
    public void MalformedLocatorShouldKeepInvalidManifestContractWithoutConstruction()
    {
        using var fixture = CreateFixture(locatorGeneratorVersion: "   ");

        var result = SharpLinkAssemblyManifestLoader.TryLoad(fixture.Assembly, out var manifest);

        Ensure(!result.Succeeded && manifest is null,
            "malformed locator should publish no manifest");
        Ensure(result.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidManifest,
            $"malformed locator should keep the invalid-manifest error code: {result.Error}");
        Ensure(result.Error!.Message.Contains("not a valid current self-describing locator", StringComparison.Ordinal),
            "malformed-locator diagnostic contract should remain stable");
        AssertOwnerFields(result.Error, fixture.Assembly);
        Ensure(fixture.State.ConstructorCalls == 0 && fixture.State.ShapeReads == 0,
            "malformed locator must fail before construction or shape validation");
    }

    private static LocatorFixture CreateFixture(
        int locatorApiVersion = SharpLinkGeneratedManifestVersions.Api,
        int locatorProtocolVersion = SharpLinkGeneratedManifestVersions.Protocol,
        string locatorGeneratorVersion = CurrentGeneratorVersion,
        string locatorAbiIdentity = SharpLinkGeneratedManifestVersions.AbiIdentity,
        int manifestApiVersion = SharpLinkGeneratedManifestVersions.Api,
        int manifestProtocolVersion = SharpLinkGeneratedManifestVersions.Protocol,
        string manifestGeneratorVersion = CurrentGeneratorVersion,
        bool malformedShape = false,
        bool includeLocator = true)
    {
        var id = Interlocked.Increment(ref _fixtureId);
        var assemblyName = $"SharpLink.GeneratedLocatorFixture.{id}";
        var state = new LocatorFixtureState(
            manifestApiVersion,
            manifestProtocolVersion,
            manifestGeneratorVersion,
            malformedShape);
        LocatorFixtureStateRegistry.Add(assemblyName, state);
        try
        {
            var assembly = new PersistedAssemblyBuilder(
                new AssemblyName(assemblyName),
                typeof(object).Assembly);
            var module = assembly.DefineDynamicModule($"GeneratedLocatorFixture.{id}");
            var type = module.DefineType(
                $"SharpLink.Generated.LocatorManifest{id}",
                TypeAttributes.Public | TypeAttributes.Sealed,
                typeof(LocatorFixtureManifestBase));
            EmitConstructor(type);
            var manifestType = type.CreateType();

            if (includeLocator)
            {
                var locatorConstructor = typeof(SharpLinkGeneratedAssemblyManifestAttribute).GetConstructor(
                    [typeof(Type), typeof(int), typeof(int), typeof(string), typeof(string)]) ??
                    throw new MissingMethodException(
                        typeof(SharpLinkGeneratedAssemblyManifestAttribute).FullName,
                        ".ctor(Type, Int32, Int32, String, String)");
                assembly.SetCustomAttribute(new CustomAttributeBuilder(
                    locatorConstructor,
                    [manifestType, locatorApiVersion, locatorProtocolVersion, locatorGeneratorVersion, locatorAbiIdentity]));
            }

            using var image = new MemoryStream();
            assembly.Save(image);
            image.Position = 0;
            var loadContext = new LocatorFixtureLoadContext(assemblyName);
            var loadedAssembly = loadContext.LoadFromStream(image);
            return new LocatorFixture(assemblyName, loadedAssembly, loadContext, state);
        }
        catch
        {
            LocatorFixtureStateRegistry.Remove(assemblyName, state);
            throw;
        }
    }

    private static void EmitConstructor(TypeBuilder type)
    {
        var constructor = type.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            Type.EmptyTypes);
        var il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(LocatorFixtureManifestBase).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Ret);
    }

    private static void AssertVersionRejection(
        SharpLinkAssemblyRegistrationResult result,
        Assembly assembly,
        int actualApiVersion,
        int actualProtocolVersion,
        string generatorVersion)
    {
        Ensure(!result.Succeeded && result.Error?.Code ==
            SharpLinkAssemblyRegistrationErrorCode.IncompatibleManifest,
            $"unsupported locator should be incompatible: {result.Error}");
        Ensure(result.Error!.Message.Contains(
                   $"API {actualApiVersion}/{SharpLinkGeneratedManifestVersions.Api}",
                   StringComparison.Ordinal) &&
               result.Error.Message.Contains(
                   $"Protocol {actualProtocolVersion}/{SharpLinkGeneratedManifestVersions.Protocol}",
                   StringComparison.Ordinal) &&
               result.Error.Message.Contains(generatorVersion, StringComparison.Ordinal) &&
               result.Error.Message.Contains("delete stale generated outputs", StringComparison.Ordinal) &&
               result.Error.Message.Contains("regenerate and rebuild", StringComparison.Ordinal),
            "unsupported locator diagnostic should carry both versions, Generator, and action");
        AssertOwnerFields(result.Error, assembly);
    }

    private static void AssertOwnerFields(SharpLinkAssemblyRegistrationError error, Assembly assembly)
    {
        Ensure(error.IncomingAssembly == assembly.FullName,
            "locator diagnostic should carry the incoming Assembly identity");
        Ensure(error.IncomingLoadContext == SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(assembly),
            "locator diagnostic should carry the incoming ALC identity");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class LocatorFixture : IDisposable
    {
        private readonly string _assemblyName;
        private readonly LocatorFixtureLoadContext _loadContext;

        internal LocatorFixture(
            string assemblyName,
            Assembly assembly,
            LocatorFixtureLoadContext loadContext,
            LocatorFixtureState state)
        {
            _assemblyName = assemblyName;
            Assembly = assembly;
            _loadContext = loadContext;
            State = state;
        }

        internal Assembly Assembly { get; }

        internal LocatorFixtureState State { get; }

        internal WeakReference CreateLoadContextWeakReference()
            => new(_loadContext, trackResurrection: false);

        public void Dispose()
        {
            LocatorFixtureStateRegistry.Remove(_assemblyName, State);
            _loadContext.Unload();
        }
    }
}

public abstract class LocatorFixtureManifestBase : ISharpLinkGeneratedAssemblyManifest
{
    public LocatorFixtureManifestBase()
        => Interlocked.Increment(ref State.ConstructorCalls);

    private LocatorFixtureState State
        => LocatorFixtureStateRegistry.Get(GetType().Assembly.GetName().Name!);

    public int ApiVersion => State.ApiVersion;

    public int ProtocolVersion => State.ProtocolVersion;

    public string GeneratorVersion => State.GeneratorVersion;

    public Assembly OwnerAssembly => GetType().Assembly;

    public string CompileTimeDescriptor
    {
        get
        {
            Interlocked.Increment(ref State.ShapeReads);
            return State.MalformedShape ? string.Empty : "phase17-valid-descriptor";
        }
    }

    public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => ReadShape<SharpLinkGeneratedContractDescriptor>();

    public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => ReadShape<SharpLinkGeneratedServiceDescriptor>();

    public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => ReadShape<IRpcGeneratedCodecFactory>();

    public IReadOnlyList<string> Dependencies => ReadShape<string>();

    private IReadOnlyList<T> ReadShape<T>()
    {
        Interlocked.Increment(ref State.ShapeReads);
        return Array.Empty<T>();
    }
}

internal sealed class LocatorFixtureState(
    int apiVersion,
    int protocolVersion,
    string generatorVersion,
    bool malformedShape)
{
    internal int ApiVersion { get; } = apiVersion;
    internal int ProtocolVersion { get; } = protocolVersion;
    internal string GeneratorVersion { get; } = generatorVersion;
    internal bool MalformedShape { get; } = malformedShape;
    internal int ConstructorCalls;
    internal int ShapeReads;
}

internal static class LocatorFixtureStateRegistry
{
    private static readonly ConcurrentDictionary<string, LocatorFixtureState> States =
        new(StringComparer.Ordinal);

    internal static void Add(string assemblyName, LocatorFixtureState state)
    {
        if (!States.TryAdd(assemblyName, state))
            throw new InvalidOperationException($"Locator fixture '{assemblyName}' already exists.");
    }

    internal static LocatorFixtureState Get(string assemblyName)
        => States.TryGetValue(assemblyName, out var state)
            ? state
            : throw new InvalidOperationException($"Locator fixture '{assemblyName}' was not registered.");

    internal static void Remove(string assemblyName, LocatorFixtureState state)
    {
        if (!States.TryRemove(assemblyName, out var removed) || !ReferenceEquals(removed, state))
            throw new InvalidOperationException($"Locator fixture '{assemblyName}' was not released exactly once.");
    }
}

internal sealed class LocatorFixtureLoadContext(string name) : AssemblyLoadContext(name, isCollectible: true)
{
    protected override Assembly? Load(AssemblyName assemblyName)
        => Default.Assemblies.FirstOrDefault(candidate =>
            AssemblyName.ReferenceMatchesDefinition(candidate.GetName(), assemblyName));
}
