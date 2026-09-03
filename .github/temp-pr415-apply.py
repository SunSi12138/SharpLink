from pathlib import Path


def replace(path: str, old: str, new: str, count: int = 1) -> None:
    p = Path(path)
    text = p.read_text()
    if old not in text:
        raise SystemExit(f"expected text not found in {path}: {old[:120]!r}")
    p.write_text(text.replace(old, new, count))


integration = Path('test/SharpLink.IntegrationTests/RuntimeAssemblyIntegrationTests.cs')
if 'SameFullNameReferencedCodecDependencyShouldRequireExactGenerationOnClientAndServer' in integration.read_text():
    raise SystemExit(0)

for path in (
    'src/SharpLink.Client/SharpLinkClient.AssemblyRegistration.cs',
    'src/SharpLink.Server/SharpLinkServer.AssemblyRegistration.cs'):
    replace(
        path,
        '''        var oldIdentity = oldModule.Manifest.OwnerAssembly.FullName;
        var newIdentity = incoming.OwnerAssembly.FullName;
        foreach (var candidate in _dynamicModules.Values)
        {
            if (!ReferenceEquals(candidate, oldModule) &&
                ManifestDependsOn(candidate.Manifest, oldIdentity))''',
        '''        var oldAssembly = oldModule.Manifest.OwnerAssembly;
        var oldIdentity = oldAssembly.FullName;
        var newIdentity = incoming.OwnerAssembly.FullName;
        foreach (var candidate in _dynamicModules.Values)
        {
            if (!ReferenceEquals(candidate, oldModule) &&
                ManifestDependsOn(candidate.Manifest, oldAssembly))''')
    replace(
        path,
        '''    private static bool ManifestDependsOn(ISharpLinkGeneratedAssemblyManifest manifest, string? identity)
        => identity is not null && EnumerateManifestDependencies(manifest)
            .Any(dependency => string.Equals(dependency, identity, StringComparison.Ordinal));''',
        '''    private static bool ManifestDependsOn(
        ISharpLinkGeneratedAssemblyManifest manifest,
        Assembly ownerAssembly)
    {
        var identity = ownerAssembly.FullName;
        if (identity is not null && EnumerateManifestDependencies(manifest)
            .Any(dependency => string.Equals(dependency, identity, StringComparison.Ordinal)))
        {
            return true;
        }

        if (manifest is not ISharpLinkReferencedCodecDependencyManifest dependencyManifest ||
            dependencyManifest.ReferencedCodecDependencies is not { } referencedDependencies)
        {
            return false;
        }

        return referencedDependencies.Any(dependency =>
            dependency is not null &&
            dependency.TargetType is { } targetType &&
            ReferenceEquals(targetType.Assembly, ownerAssembly));
    }''')

replace(
    'src/SharpLink.Client/SharpLinkClient.AssemblyDrain.cs',
    '''            Volatile.Write(ref _proxies, nextProxies);
            _runtimeContext.PublishGeneratedCodecs(nextFactories);
            _dynamicModules.Remove(assembly);''',
    '''            _runtimeContext.PublishGeneratedCodecs(nextFactories);
            Volatile.Write(ref _proxies, nextProxies);
            _dynamicModules.Remove(assembly);''')
replace(
    'src/SharpLink.Client/SharpLinkClient.AssemblyDrain.cs',
    '''    private void EnsureNoDynamicDependants(SharpLinkDynamicModule module)
    {
        var identity = module.Manifest.OwnerAssembly.FullName;
        foreach (var candidate in _dynamicModules.Values)
        {
            if (!ReferenceEquals(candidate, module) &&
                ManifestDependsOn(candidate.Manifest, identity))
                throw new InvalidOperationException(
                    $"Assembly '{identity}' cannot be unregistered while '{candidate.Manifest.OwnerAssembly.FullName}' depends on it.");
        }
    }''',
    '''    private void EnsureNoDynamicDependants(SharpLinkDynamicModule module)
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
    }''')

replace(
    'src/SharpLink.Server/SharpLinkServer.AssemblyDrain.cs',
    '''            Volatile.Write(ref _services, nextServices);
            _runtimeContext.PublishGeneratedCodecs(nextFactories);
            _dynamicModules.Remove(assembly);''',
    '''            _runtimeContext.PublishGeneratedCodecs(nextFactories);
            Volatile.Write(ref _services, nextServices);
            _dynamicModules.Remove(assembly);''')
replace(
    'src/SharpLink.Server/SharpLinkServer.AssemblyDrain.cs',
    '''    private void EnsureNoDynamicDependants(SharpLinkDynamicModule module)
    {
        var identity = module.Manifest.OwnerAssembly.FullName;
        foreach (var candidate in _dynamicModules.Values)
        {
            if (ReferenceEquals(candidate, module))
                continue;
            if (ManifestDependsOn(candidate.Manifest, identity))
            {
                throw new InvalidOperationException(
                    $"Assembly '{identity}' cannot be unregistered while '{candidate.Manifest.OwnerAssembly.FullName}' depends on it.");
            }
        }
    }''',
    '''    private void EnsureNoDynamicDependants(SharpLinkDynamicModule module)
    {
        var ownerAssembly = module.Manifest.OwnerAssembly;
        var identity = ownerAssembly.FullName;
        foreach (var candidate in _dynamicModules.Values)
        {
            if (ReferenceEquals(candidate, module))
                continue;
            if (ManifestDependsOn(candidate.Manifest, ownerAssembly))
            {
                throw new InvalidOperationException(
                    $"Assembly '{identity}' cannot be unregistered while '{candidate.Manifest.OwnerAssembly.FullName}' depends on it.");
            }
        }
    }''')

provider_dir = Path('test/SharpLink.ReferencedCodecProvider')
provider_dir.mkdir(exist_ok=True)
(provider_dir / 'SharpLink.ReferencedCodecProvider.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\\..\\src\\SharpLink.Abstractions\\SharpLink.Abstractions.csproj" />
  </ItemGroup>
</Project>
''')
(provider_dir / 'ReferencedCodecProvider.cs').write_text('''using System.Buffers;
using SharpLink.Abstractions;

[assembly: SharpLinkGeneratedAssemblyManifestAttribute(
    typeof(SharpLink.ReferencedCodecProvider.ProviderManifest),
    SharpLinkGeneratedManifestVersions.Api,
    SharpLinkGeneratedManifestVersions.Protocol,
    "test",
    SharpLinkGeneratedManifestVersions.AbiIdentity)]

namespace SharpLink.ReferencedCodecProvider;

public readonly record struct Payload(int Value);

public sealed class ProviderManifest : ISharpLinkGeneratedAssemblyManifest
{
    public const ulong CodecHashHigh = 0x1122334455667788UL;
    public const ulong CodecHashLow = 0x8877665544332211UL;

    private static readonly IReadOnlyList<IRpcGeneratedCodecFactory> Factories =
        new IRpcGeneratedCodecFactory[] { new PayloadFactory() };

    public ProviderManifest() { }

    public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
    public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
    public string GeneratorVersion => "test";
    public System.Reflection.Assembly OwnerAssembly => typeof(ProviderManifest).Assembly;
    public RpcHash128 RpcAssemblyHash => new(0xAABBCCDDEEFF0011UL, 0x1100FFEEDDCCBBAAUL);
    public string CompileTimeDescriptor => "referenced-codec-provider";
    public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
    public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
    public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => Factories;
    public IReadOnlyList<string> Dependencies => [];

    private sealed class PayloadFactory : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(Payload);
        public RpcHash128 CodecHash => new(CodecHashHigh, CodecHashLow);
        public string? AdapterId => null;
        public IRpcCodecAdapter? Adapter => null;
        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
        {
            _ = provider;
            if (adapterScope is not null)
                throw new ArgumentException("Native Codec factory does not accept an adapter scope.", nameof(adapterScope));
            return new PayloadCodec();
        }
        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<Payload>;
    }

    private sealed class PayloadCodec : IRpcCodec<Payload>
    {
        public void Serialize(in Payload value, IBufferWriter<byte> buffer)
        {
            _ = value;
            _ = buffer;
        }

        public Payload Deserialize(in ReadOnlySequence<byte> buffer)
        {
            _ = buffer;
            return default;
        }
    }
}
''')

consumer_dir = Path('test/SharpLink.ReferencedCodecConsumer')
consumer_dir.mkdir(exist_ok=True)
(consumer_dir / 'SharpLink.ReferencedCodecConsumer.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\\..\\src\\SharpLink.Abstractions\\SharpLink.Abstractions.csproj" />
    <ProjectReference Include="..\\SharpLink.ReferencedCodecProvider\\SharpLink.ReferencedCodecProvider.csproj" />
  </ItemGroup>
</Project>
''')
(consumer_dir / 'ReferencedCodecConsumer.cs').write_text('''using SharpLink.Abstractions;
using SharpLink.ReferencedCodecProvider;

[assembly: SharpLinkGeneratedAssemblyManifestAttribute(
    typeof(SharpLink.ReferencedCodecConsumer.ConsumerManifest),
    SharpLinkGeneratedManifestVersions.Api,
    SharpLinkGeneratedManifestVersions.Protocol,
    "test",
    SharpLinkGeneratedManifestVersions.AbiIdentity)]

namespace SharpLink.ReferencedCodecConsumer;

public sealed class ConsumerManifest : ISharpLinkGeneratedAssemblyManifest, ISharpLinkReferencedCodecDependencyManifest
{
    private static readonly IReadOnlyList<SharpLinkReferencedCodecDependency> Referenced =
        new SharpLinkReferencedCodecDependency[]
        {
            new(
                typeof(Payload),
                new RpcHash128(ProviderManifest.CodecHashHigh, ProviderManifest.CodecHashLow))
        };

    public ConsumerManifest() { }

    public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
    public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
    public string GeneratorVersion => "test";
    public System.Reflection.Assembly OwnerAssembly => typeof(ConsumerManifest).Assembly;
    public RpcHash128 RpcAssemblyHash => new(0x1234567890ABCDEFUL, 0xFEDCBA0987654321UL);
    public string CompileTimeDescriptor => "referenced-codec-consumer";
    public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
    public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
    public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
    public IReadOnlyList<string> Dependencies => [];
    public IReadOnlyList<SharpLinkReferencedCodecDependency> ReferencedCodecDependencies => Referenced;
}
''')

replace(
    'test/SharpLink.IntegrationTests/SharpLink.IntegrationTests.csproj',
    '''    <ProjectReference Include="..\\SharpLink.DynamicServices\\SharpLink.DynamicServices.csproj" ReferenceOutputAssembly="false" />''',
    '''    <ProjectReference Include="..\\SharpLink.DynamicServices\\SharpLink.DynamicServices.csproj" ReferenceOutputAssembly="false" />
    <ProjectReference Include="..\\SharpLink.ReferencedCodecProvider\\SharpLink.ReferencedCodecProvider.csproj" ReferenceOutputAssembly="false" />
    <ProjectReference Include="..\\SharpLink.ReferencedCodecConsumer\\SharpLink.ReferencedCodecConsumer.csproj" ReferenceOutputAssembly="false" />''')

replace(
    'test/SharpLink.IntegrationTests/RuntimeAssemblyIntegrationTests.cs',
    '''public sealed class RuntimeAssemblyIntegrationTests
{
''',
    '''public sealed class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    public async Task SameFullNameReferencedCodecDependencyShouldRequireExactGenerationOnClientAndServer()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        var directory = GetReferencedCodecOutputDirectory();
        var firstContext = new PluginLoadContext("referenced-codec-generation-1", directory);
        var secondContext = new PluginLoadContext("referenced-codec-generation-2", directory);
        try
        {
            var providerPath = Path.Combine(directory, "SharpLink.ReferencedCodecProvider.dll");
            var consumerPath = Path.Combine(directory, "SharpLink.ReferencedCodecConsumer.dll");
            var provider1 = firstContext.LoadFromAssemblyPath(providerPath);
            var provider2 = secondContext.LoadFromAssemblyPath(providerPath);
            var consumer2 = secondContext.LoadFromAssemblyPath(consumerPath);

            Ensure(provider1.FullName == provider2.FullName && !ReferenceEquals(provider1, provider2),
                "test setup must load two distinct provider generations with the same Assembly.FullName");
            var consumerManifestType = consumer2.GetType(
                "SharpLink.ReferencedCodecConsumer.ConsumerManifest",
                throwOnError: true)!;
            var consumerManifest = (ISharpLinkReferencedCodecDependencyManifest)Activator.CreateInstance(
                consumerManifestType)!;
            var typedDependency = consumerManifest.ReferencedCodecDependencies.Single();
            Ensure(ReferenceEquals(typedDependency.TargetType.Assembly, provider2),
                "consumer generation 2 must retain the exact provider generation selected by its runtime Type binding");

            Ensure(harness.Client.RegisterAssembly(provider1).Succeeded,
                "client registers generation-1 provider");
            Ensure(harness.Server.RegisterAssembly(provider1).Succeeded,
                "server registers generation-1 provider");

            var wrongClient = harness.Client.RegisterAssembly(consumer2);
            Ensure(!wrongClient.Succeeded &&
                   wrongClient.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidManifest &&
                   wrongClient.Error.Message.Contains("exact bound runtime Type/assembly generation", StringComparison.Ordinal),
                $"client must reject generation-2 consumer when only same-FullName generation-1 provider is registered: {wrongClient.Error}");
            var wrongServer = harness.Server.RegisterAssembly(consumer2);
            Ensure(!wrongServer.Succeeded &&
                   wrongServer.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidManifest &&
                   wrongServer.Error.Message.Contains("exact bound runtime Type/assembly generation", StringComparison.Ordinal),
                $"server must reject generation-2 consumer when only same-FullName generation-1 provider is registered: {wrongServer.Error}");

            Ensure((await harness.Client.UnregisterAssemblyAsync(provider1, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "client releases generation-1 provider after rejected consumer");
            Ensure((await harness.Server.UnregisterAssemblyAsync(provider1, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "server releases generation-1 provider after rejected consumer");

            Ensure(harness.Client.RegisterAssembly(provider2).Succeeded,
                "client registers exact generation-2 provider");
            Ensure(harness.Server.RegisterAssembly(provider2).Succeeded,
                "server registers exact generation-2 provider");
            Ensure(harness.Client.RegisterAssembly(consumer2).Succeeded,
                "client accepts consumer with exact bound provider generation and expected CodecHash");
            Ensure(harness.Server.RegisterAssembly(consumer2).Succeeded,
                "server accepts consumer with exact bound provider generation and expected CodecHash");

            try
            {
                _ = await harness.Client.UnregisterAssemblyAsync(provider2, TimeSpan.FromSeconds(2));
                throw new Exception("assert failed: client must reject provider unregister while exact typed consumer depends on it");
            }
            catch (InvalidOperationException exception)
            {
                Ensure(exception.Message.Contains("depends on it", StringComparison.Ordinal),
                    "client reverse dependency check uses exact provider Assembly generation");
            }
            try
            {
                _ = await harness.Server.UnregisterAssemblyAsync(provider2, TimeSpan.FromSeconds(2));
                throw new Exception("assert failed: server must reject provider unregister while exact typed consumer depends on it");
            }
            catch (InvalidOperationException exception)
            {
                Ensure(exception.Message.Contains("depends on it", StringComparison.Ordinal),
                    "server reverse dependency check uses exact provider Assembly generation");
            }

            Ensure((await harness.Client.UnregisterAssemblyAsync(consumer2, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "client releases typed consumer before provider");
            Ensure((await harness.Server.UnregisterAssemblyAsync(consumer2, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "server releases typed consumer before provider");
            Ensure((await harness.Client.UnregisterAssemblyAsync(provider2, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "client releases exact provider after dependant removal");
            Ensure((await harness.Server.UnregisterAssemblyAsync(provider2, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "server releases exact provider after dependant removal");
        }
        finally
        {
            firstContext.Unload();
            secondContext.Unload();
        }
    }

''')

replace(
    'test/SharpLink.IntegrationTests/RuntimeAssemblyIntegrationTests.cs',
    '''    private sealed class PluginLoadContext(string name, string directory)
        : AssemblyLoadContext(name, isCollectible: true)''',
    '''    private static string GetReferencedCodecOutputDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sharplink.slnx")))
            directory = directory.Parent;
        if (directory is null)
            throw new DirectoryNotFoundException("SharpLink workspace root was not found.");
        return Path.Combine(
            directory.FullName,
            "test",
            "SharpLink.ReferencedCodecConsumer",
            "bin",
            "Release",
            "net10.0");
    }

    private sealed class PluginLoadContext(string name, string directory)
        : AssemblyLoadContext(name, isCollectible: true)''')
