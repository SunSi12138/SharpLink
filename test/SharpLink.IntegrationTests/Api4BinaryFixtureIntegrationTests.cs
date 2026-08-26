using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.IntegrationTests;

public sealed class Api4BinaryFixtureIntegrationTests
{
    private const string FixtureSha256 =
        "5a6adda8bef11941e1175505f090ebf8db304f268bfa157ba4021e603b180d61";

    [Test]
    [NotInParallel]
    public async Task FrozenDevelopmentApi4BinaryShouldBeRejectedByExactAbiIdentity()
    {
        var weakContext = RejectFixture();
        for (var attempt = 0; attempt < 20 && weakContext.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(20);
        }

        Ensure(!weakContext.IsAlive,
            "the rejected development API4 fixture must not root its collectible load context");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RejectFixture()
    {
        var loadContext = new FixtureLoadContext("api4-abi-collision-sentinel");
        var weakContext = new WeakReference(loadContext, trackResurrection: false);
        using var assemblyStream = new MemoryStream(ReadFixtureAssembly(), writable: false);
        var assembly = loadContext.LoadFromStream(assemblyStream);

        var result = SharpLinkAssemblyManifestLoader.TryLoad(assembly, out var manifest);
        Ensure(!result.Succeeded && manifest is null,
            "the pre-#287 development API4 binary must not be positively identified as the current API4 ABI");
        Ensure(result.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.IncompatibleManifest,
            $"the API4 ABI collision sentinel should fail as IncompatibleManifest: {result.Error}");
        Ensure(result.Error!.Message.Contains("API 4/4", StringComparison.Ordinal) &&
               result.Error.Message.Contains("<missing: pre-current ABI locator>", StringComparison.Ordinal) &&
               result.Error.Message.Contains(SharpLinkGeneratedManifestVersions.AbiIdentity, StringComparison.Ordinal),
            "the rejection must distinguish two incompatible API4 shapes by exact ABI identity");

        assembly = null!;
        loadContext.Unload();
        return weakContext;
    }

    private static byte[] ReadFixtureAssembly()
    {
        var root = FindWorkspaceRoot();
        var encoded = File.ReadAllText(Path.Combine(
            root, "test", "fixtures", "generated-api4", "SharpLink.Api4Fixture.dll.gz.b64"));
        var compressed = Convert.FromBase64String(encoded);
        using var compressedStream = new MemoryStream(compressed, writable: false);
        using var gzip = new GZipStream(compressedStream, CompressionMode.Decompress);
        using var assemblyStream = new MemoryStream();
        gzip.CopyTo(assemblyStream);
        var assembly = assemblyStream.ToArray();
        Ensure(string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(assembly)), FixtureSha256, StringComparison.Ordinal),
            "the frozen development API4 fixture checksum must match its provenance");
        return assembly;
    }

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sharplink.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("SharpLink workspace root was not found.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception($"assert failed: {message}");
    }

    private sealed class FixtureLoadContext(string name) : AssemblyLoadContext(name, isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var shared = Default.Assemblies.FirstOrDefault(candidate =>
                AssemblyName.ReferenceMatchesDefinition(candidate.GetName(), assemblyName));
            if (shared is not null)
                return shared;
            var path = Path.Combine(AppContext.BaseDirectory, $"{assemblyName.Name}.dll");
            return File.Exists(path) ? Default.LoadFromAssemblyPath(path) : null;
        }
    }
}
