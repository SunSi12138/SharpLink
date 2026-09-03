from pathlib import Path


def replace_once(path, old, new):
    p = Path(path)
    text = p.read_text()
    if old not in text:
        raise SystemExit(f"missing expected block in {path}: {old[:120]!r}")
    text = text.replace(old, new, 1)
    p.write_text(text)

# 1. Persist referenced final CodecHash identities into contract baselines.
path = "src/SharpLink.Generator/RpcGenerator.ContractManifest.cs"
replace_once(path,
'''        var opaqueCodecHashes = codecsByType
            .Where(static pair => pair.Value.Kind is GeneratedCodecKind.Custom or GeneratedCodecKind.Adapter)
            .ToDictionary(
                static pair => pair.Key,
                static pair => GetCodecHash(pair.Value),
                StringComparer.Ordinal);
''',
'''        var contractCodecHashes = codecsByType
            .Where(static pair => pair.Value.Kind is GeneratedCodecKind.Custom or GeneratedCodecKind.Adapter)
            .ToDictionary(
                static pair => pair.Key,
                static pair => GetCodecHash(pair.Value),
                StringComparer.Ordinal);
        foreach (var codecHash in codecHashes
                     .Where(static item => item.IsReferenced)
                     .OrderBy(static item => item.TypeName, StringComparer.Ordinal))
        {
            contractCodecHashes[RemoveGlobalPrefix(codecHash.TypeName)] =
                new RpcHashValue(codecHash.High, codecHash.Low).ToHex();
        }
''')
p = Path(path)
text = p.read_text().replace("GetOpaqueCodecHash(typeName, opaqueCodecHashes)", "GetContractCodecHash(typeName, contractCodecHashes)")
text = text.replace("GetOpaqueCodecHash(responseType, opaqueCodecHashes)", "GetContractCodecHash(responseType, contractCodecHashes)")
text = text.replace("GetOpaqueCodecHash(member.TypeName, opaqueCodecHashes)", "GetContractCodecHash(member.TypeName, contractCodecHashes)")
p.write_text(text)
replace_once(path,
'''        foreach (var codec in codecs.OrderBy(static item => item.TypeName, StringComparer.Ordinal))
        {
            document.Codecs.Add(new ContractManifestCodec
            {
                Type = RemoveGlobalPrefix(codec.TypeName),
                Kind = codec.Kind.ToString(),
                CodecHash = GetCodecHash(codec),
                SourceLocation = codec.Location
            });
        }

        var enums = new Dictionary<string, ContractManifestEnum>(StringComparer.Ordinal);
''',
'''        foreach (var codec in codecs.OrderBy(static item => item.TypeName, StringComparer.Ordinal))
        {
            document.Codecs.Add(new ContractManifestCodec
            {
                Type = RemoveGlobalPrefix(codec.TypeName),
                Kind = codec.Kind.ToString(),
                CodecHash = GetCodecHash(codec),
                SourceLocation = codec.Location
            });
        }
        var emittedCodecTypes = new HashSet<string>(
            document.Codecs.Select(static item => item.Type),
            StringComparer.Ordinal);
        foreach (var codecHash in codecHashes
                     .Where(static item => item.IsReferenced)
                     .OrderBy(static item => item.TypeName, StringComparer.Ordinal))
        {
            var typeName = RemoveGlobalPrefix(codecHash.TypeName);
            if (!emittedCodecTypes.Add(typeName))
                continue;
            document.Codecs.Add(new ContractManifestCodec
            {
                Type = typeName,
                Kind = "Referenced",
                CodecHash = new RpcHashValue(codecHash.High, codecHash.Low).ToHex()
            });
        }

        var enums = new Dictionary<string, ContractManifestEnum>(StringComparer.Ordinal);
''')

path = "src/SharpLink.Generator/RpcGenerator.ContractManifest.Infrastructure.cs"
replace_once(path,
'''        var opaqueCodecTypes = new HashSet<string>(
            manifest.Codecs
                .Where(static codec => codec is not null &&
                    (string.Equals(codec.Kind, "Custom", StringComparison.Ordinal) ||
                     string.Equals(codec.Kind, "Adapter", StringComparison.Ordinal)) &&
                    IsValidCodecHash(codec.CodecHash))
                .Select(static codec => codec.Type),
            StringComparer.Ordinal);

        bool HasValueIdentity(string type, string? codecHash)
            => !opaqueCodecTypes.Contains(type) || IsValidCodecHash(codecHash);
''',
'''        var identityBoundCodecTypes = new HashSet<string>(
            manifest.Codecs
                .Where(static codec => codec is not null &&
                    (string.Equals(codec.Kind, "Custom", StringComparison.Ordinal) ||
                     string.Equals(codec.Kind, "Adapter", StringComparison.Ordinal) ||
                     string.Equals(codec.Kind, "Referenced", StringComparison.Ordinal)) &&
                    IsValidCodecHash(codec.CodecHash))
                .Select(static codec => codec.Type),
            StringComparer.Ordinal);

        bool HasValueIdentity(string type, string? codecHash)
            => !identityBoundCodecTypes.Contains(type) || IsValidCodecHash(codecHash);
''')
replace_once(path,
'''    private static string? GetOpaqueCodecHash(
        string typeName,
        IReadOnlyDictionary<string, string> opaqueCodecHashes)
        => opaqueCodecHashes.TryGetValue(RemoveGlobalPrefix(typeName), out var codecHash)
''',
'''    private static string? GetContractCodecHash(
        string typeName,
        IReadOnlyDictionary<string, string> contractCodecHashes)
        => contractCodecHashes.TryGetValue(RemoveGlobalPrefix(typeName), out var codecHash)
''')

path = "src/SharpLink.Generator/RpcGenerator.ContractManifest.Compatibility.cs"
replace_once(path,
'''            var opaque =
                string.Equals(oldCodec.Kind, "Custom", StringComparison.Ordinal) ||
                string.Equals(oldCodec.Kind, "Adapter", StringComparison.Ordinal) ||
                string.Equals(newCodec.Kind, "Custom", StringComparison.Ordinal) ||
                string.Equals(newCodec.Kind, "Adapter", StringComparison.Ordinal);
            if (!opaque || string.Equals(oldCodec.CodecHash, newCodec.CodecHash, StringComparison.Ordinal))
''',
'''            var identityBound =
                string.Equals(oldCodec.Kind, "Custom", StringComparison.Ordinal) ||
                string.Equals(oldCodec.Kind, "Adapter", StringComparison.Ordinal) ||
                string.Equals(oldCodec.Kind, "Referenced", StringComparison.Ordinal) ||
                string.Equals(newCodec.Kind, "Custom", StringComparison.Ordinal) ||
                string.Equals(newCodec.Kind, "Adapter", StringComparison.Ordinal) ||
                string.Equals(newCodec.Kind, "Referenced", StringComparison.Ordinal);
            if (!identityBound || string.Equals(oldCodec.CodecHash, newCodec.CodecHash, StringComparison.Ordinal))
''')

# 2. Put all module dependency relations and shutdown ordering in one binding-aware Runtime helper.
path = "src/SharpLink.Runtime/GeneratedAssembly/SharpLinkGeneratedDependencyBinding.cs"
replace_once(path,
'''    internal static bool Matches(
        Assembly ownerAssembly,
        string dependencyIdentity,
        Assembly candidateAssembly)
        => ReferenceEquals(Resolve(ownerAssembly, dependencyIdentity), candidateAssembly);
}
''',
'''    internal static bool Matches(
        Assembly ownerAssembly,
        string dependencyIdentity,
        Assembly candidateAssembly)
        => ReferenceEquals(Resolve(ownerAssembly, dependencyIdentity), candidateAssembly);

    internal static bool ManifestDependsOn(
        ISharpLinkGeneratedAssemblyManifest manifest,
        Assembly ownerAssembly)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(ownerAssembly);
        foreach (var dependency in manifest.Dependencies)
        {
            if (Matches(manifest.OwnerAssembly, dependency, ownerAssembly))
                return true;
        }
        foreach (var dependency in manifest.ContractDependencies)
        {
            if (Matches(manifest.OwnerAssembly, dependency, ownerAssembly))
                return true;
        }
        if (manifest is not ISharpLinkReferencedCodecDependencyManifest dependencyManifest ||
            dependencyManifest.ReferencedCodecDependencies is not { } referencedDependencies)
        {
            return false;
        }
        for (var index = 0; index < referencedDependencies.Count; index++)
        {
            var dependency = referencedDependencies[index];
            if (dependency is not null &&
                dependency.TargetType is { } targetType &&
                ReferenceEquals(targetType.Assembly, ownerAssembly))
            {
                return true;
            }
        }
        return false;
    }

    internal static int[] GetDependantsFirstOrder(
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        var remaining = new bool[manifests.Count];
        Array.Fill(remaining, true);
        var order = new int[manifests.Count];
        for (var outputIndex = 0; outputIndex < order.Length; outputIndex++)
        {
            var selected = -1;
            for (var candidate = 0; candidate < manifests.Count; candidate++)
            {
                if (!remaining[candidate])
                    continue;
                var candidateAssembly = manifests[candidate].OwnerAssembly;
                var hasRemainingDependant = false;
                for (var dependant = 0; dependant < manifests.Count; dependant++)
                {
                    if (dependant == candidate || !remaining[dependant])
                        continue;
                    if (ManifestDependsOn(manifests[dependant], candidateAssembly))
                    {
                        hasRemainingDependant = true;
                        break;
                    }
                }
                if (!hasRemainingDependant)
                {
                    selected = candidate;
                    break;
                }
            }
            if (selected < 0)
            {
                for (var candidate = manifests.Count - 1; candidate >= 0; candidate--)
                {
                    if (remaining[candidate])
                    {
                        selected = candidate;
                        break;
                    }
                }
            }
            order[outputIndex] = selected;
            remaining[selected] = false;
        }
        return order;
    }
}
''')

manifest_dep_old = '''    private static bool ManifestDependsOn(
        ISharpLinkGeneratedAssemblyManifest manifest,
        Assembly ownerAssembly)
    {
        foreach (var dependency in EnumerateManifestDependencies(manifest))
        {
            if (SharpLinkGeneratedDependencyBinding.Matches(
                    manifest.OwnerAssembly,
                    dependency,
                    ownerAssembly))
            {
                return true;
            }
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
    }
'''
manifest_dep_new = '''    private static bool ManifestDependsOn(
        ISharpLinkGeneratedAssemblyManifest manifest,
        Assembly ownerAssembly)
        => SharpLinkGeneratedDependencyBinding.ManifestDependsOn(manifest, ownerAssembly);
'''
replace_once("src/SharpLink.Client/SharpLinkClient.AssemblyRegistration.cs", manifest_dep_old, manifest_dep_new)
replace_once("src/SharpLink.Server/SharpLinkServer.AssemblyRegistration.cs", manifest_dep_old, manifest_dep_new)

path = "src/SharpLink.Client/SharpLinkClient.cs"
replace_once(path,
'''        var identities = new string[modules.Length];
        var dependencies = new string[modules.Length][];
        for (var index = 0; index < modules.Length; index++)
        {
            var manifest = modules[index].Manifest;
            identities[index] = manifest.OwnerAssembly.FullName ??
                                manifest.OwnerAssembly.GetName().Name ??
                                string.Empty;
            dependencies[index] = EnumerateManifestDependencies(manifest).ToArray();
        }

        var order = GetShutdownDependencyOrder(identities, dependencies);
''',
'''        var manifests = modules.Select(static module => module.Manifest).ToArray();
        var order = SharpLinkGeneratedDependencyBinding.GetDependantsFirstOrder(manifests);
''')

path = "src/SharpLink.Server/SharpLinkServer.AssemblyDrain.cs"
replace_once(path,
'''        List<Exception>? failures = null;
        for (var index = 0; index < modules.Length; index++)
        {
            var pair = modules[index];
''',
'''        var manifests = modules.Select(static pair => pair.Value.Manifest).ToArray();
        var order = SharpLinkGeneratedDependencyBinding.GetDependantsFirstOrder(manifests);
        List<Exception>? failures = null;
        for (var index = 0; index < order.Length; index++)
        {
            var pair = modules[order[index]];
''')

# 3. Generator regression: direct + nested referenced H1 -> H2 baselines must fail.
path = "test/SharpLink.Generator.Tests/ContractManifestGeneratorTestHelpers.cs"
replace_once(path,
'''    private static ContractGeneratorResult RunContractGenerator(
        string source,
        string? baseline = null,
        string? outputPath = null)
''',
'''    private static ContractGeneratorResult RunContractGenerator(
        string source,
        string? baseline = null,
        string? outputPath = null,
        params MetadataReference[] additionalReferences)
''')
replace_once(path,
'''            [syntaxTree],
            GetPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
''',
'''            [syntaxTree],
            GetPlatformReferences().Concat(additionalReferences),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
''')

path = "test/SharpLink.Generator.Tests/RpcCodecTenthReviewRegressionTests.cs"
insert_before = '''    [Test]
    public Task ReferencedCodecHashShouldRequireCurrentGeneratedAbi()
'''
new_test = r'''    [Test]
    public Task ReferencedCodecHashChangeShouldFailDirectAndNestedContractBaselines()
    {
        static MetadataReference GeneratedPayloadReference(ulong low)
            => CreateMetadataReference(
                "ReferencedBaselinePayload",
                $$"""
using System;

[assembly: SharpLink.Abstractions.SharpLinkGeneratedCodecIdentityAttribute(typeof(Referenced.Payload), 0x5555555555555555UL, {{low}}UL)]
[assembly: SharpLink.Abstractions.SharpLinkGeneratedAssemblyManifestAttribute(typeof(Referenced.Manifest), 4, 2, "2.0.0-test", "sharplink-2.0-api4-rpcchannel-codec-provider-v4")]

namespace SharpLink.Abstractions
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class SharpLinkGeneratedCodecIdentityAttribute : Attribute
    {
        public SharpLinkGeneratedCodecIdentityAttribute(Type targetType, ulong high, ulong low) { }
    }

    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
    public sealed class SharpLinkGeneratedAssemblyManifestAttribute : Attribute
    {
        public SharpLinkGeneratedAssemblyManifestAttribute(
            Type manifestType,
            int apiVersion,
            int protocolVersion,
            string generatorVersion,
            string abiIdentity) { }
    }
}

namespace Referenced
{
    public sealed class Payload { public int Value { get; set; } }
    public sealed class Manifest { }
}
""");

        const string directConsumer = """
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

[RpcContract]
public interface IReferencedBaselineContract : IService
{
    ValueTask<Referenced.Payload> Echo(Referenced.Payload value, CancellationToken cancellationToken);
}
""";
        const string nestedConsumer = """
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

[RpcContract]
public interface IReferencedNestedBaselineContract : IService
{
    ValueTask<List<Referenced.Payload>> Echo(List<Referenced.Payload> value, CancellationToken cancellationToken);
}
""";

        var h1 = GeneratedPayloadReference(0x1111111111111111UL);
        var h2 = GeneratedPayloadReference(0x2222222222222222UL);
        var directBaseline = RunContractGenerator(directConsumer, additionalReferences: h1).Json;
        var directChanged = RunContractGenerator(directConsumer, directBaseline, additionalReferences: h2);
        Ensure(directChanged.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            $"a direct referenced final CodecHash H1 -> H2 change must fail the contract baseline. Actual: {FormatDiagnostics(directChanged.Diagnostics)}");

        var nestedBaseline = RunContractGenerator(nestedConsumer, additionalReferences: h1).Json;
        var nestedDocument = System.Text.Json.Nodes.JsonNode.Parse(nestedBaseline)!.AsObject();
        var referencedCodec = nestedDocument["codecs"]!.AsArray()
            .Select(static item => item!.AsObject())
            .Single(static item => item["type"]!.GetValue<string>() == "Referenced.Payload");
        Ensure(referencedCodec["kind"]!.GetValue<string>() == "Referenced" &&
               IsValidCodecHashText(referencedCodec["codecHash"]?.GetValue<string>()),
            "nested referenced final Codec leaves must be persisted in the reachable Codec identity inventory");
        var nestedChanged = RunContractGenerator(nestedConsumer, nestedBaseline, additionalReferences: h2);
        Ensure(nestedChanged.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK030"),
            $"a nested referenced final CodecHash H1 -> H2 change must fail the contract baseline. Actual: {FormatDiagnostics(nestedChanged.Diagnostics)}");
        return Task.CompletedTask;
    }

'''
replace_once(path, insert_before, new_test + insert_before)

# 4. Shutdown integration: registering provider first must still release consumer first on StopAsync.
path = "test/SharpLink.IntegrationTests/RuntimeAssemblyDependencyIdentityIntegrationTests.cs"
insert_before = '''    [Test]
    [NotInParallel]
    public async Task SameFullNameDeclaredModuleDependencyShouldRequireExactBoundGenerationOnClientAndServer()
'''
shutdown_test = r'''    [Test]
    [NotInParallel]
    public async Task ReferencedCodecDependenciesShouldShutdownDependantsBeforeProvidersOnClientAndServer()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        var directory = GetProjectOutputDirectory("SharpLink.ReferencedCodecConsumer");
        var loadContext = new PluginLoadContext("referenced-codec-shutdown", directory);
        try
        {
            var provider = loadContext.LoadFromAssemblyPath(
                Path.Combine(directory, "SharpLink.ReferencedCodecProvider.dll"));
            var consumer = loadContext.LoadFromAssemblyPath(
                Path.Combine(directory, "SharpLink.ReferencedCodecConsumer.dll"));

            Ensure(harness.Client.RegisterAssembly(provider).Succeeded, "client registers shutdown provider first");
            Ensure(harness.Server.RegisterAssembly(provider).Succeeded, "server registers shutdown provider first");
            Ensure(harness.Client.RegisterAssembly(consumer).Succeeded, "client registers typed dependant second");
            Ensure(harness.Server.RegisterAssembly(consumer).Succeeded, "server registers typed dependant second");

            await harness.Client.StopAsync();
            await harness.Server.StopAsync();

            Ensure(GetDynamicModuleCount(harness.Client) == 0,
                "client StopAsync must release both typed dependant and provider without leaving the provider registered");
            Ensure(GetDynamicModuleCount(harness.Server) == 0,
                "server StopAsync must release both typed dependant and provider without leaving the provider registered");
        }
        finally
        {
            loadContext.Unload();
        }
    }

'''
replace_once(path, insert_before, shutdown_test + insert_before)
helper_marker = '''    private static async Task EnsureDependencyPreventsUnregisterAsync(
'''
helper = r'''    private static int GetDynamicModuleCount(object endpoint)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var field = endpoint.GetType().GetField("_dynamicModules", flags)
            ?? throw new InvalidOperationException($"Dynamic module registry was not available from '{endpoint.GetType()}'.");
        return ((System.Collections.IDictionary)(field.GetValue(endpoint)
            ?? throw new InvalidOperationException("Dynamic module registry was null."))).Count;
    }

'''
replace_once(path, helper_marker, helper + helper_marker)

# 5. Mobile: do not select an arbitrary simulator runtime. Match the installed .NET 11 iOS workload (26.5).
path = ".github/workflows/codec-mobile-compatibility.yml"
p = Path(path)
text = p.read_text()
old = '''      DOTNET_11_SDK: 11.0.100-preview.7.26381.103
'''
new = '''      DOTNET_11_SDK: 11.0.100-preview.7.26381.103
      IOS_SIMULATOR_RUNTIME: com.apple.CoreSimulator.SimRuntime.iOS-26-5
'''
if old not in text:
    raise SystemExit("mobile env marker missing")
text = text.replace(old, new, 1)
p.write_text(text)
replace_once(path,
'''          data = json.loads(subprocess.check_output(['xcrun', 'simctl', 'list', 'devices', 'available', '-j']))
          for runtime, devices in data['devices'].items():
              if 'iOS' not in runtime:
                  continue
              for device in devices:
                  if device.get('isAvailable') and device.get('name', '').startswith('iPhone'):
                      print(device['udid'])
                      raise SystemExit
          raise SystemExit('No available iPhone simulator found')
''',
'''          import os
          data = json.loads(subprocess.check_output(['xcrun', 'simctl', 'list', 'devices', 'available', '-j']))
          runtime = os.environ['IOS_SIMULATOR_RUNTIME']
          devices = data['devices'].get(runtime, [])
          for device in devices:
              if device.get('isAvailable') and device.get('name', '').startswith('iPhone'):
                  print(device['udid'])
                  raise SystemExit
          raise SystemExit(f'No available iPhone simulator found for required runtime {runtime}')
''')
replace_once(path,
'''          echo "IOS_SIMULATOR_UDID=$udid" >> "$GITHUB_ENV"
          xcrun simctl boot "$udid" || true
''',
'''          echo "Selected iOS simulator runtime: $IOS_SIMULATOR_RUNTIME"
          xcrun simctl list runtimes | grep 'iOS 26.5'
          echo "IOS_SIMULATOR_UDID=$udid" >> "$GITHUB_ENV"
          xcrun simctl boot "$udid" || true
''')

print("PR415 follow-up patch applied")
