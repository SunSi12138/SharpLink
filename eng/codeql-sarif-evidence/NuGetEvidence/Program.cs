// Evidence-only host. No product references; NuGet assemblies come only from the pinned SDK.
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

internal sealed class OfficialNuGetContext(string sdk) : AssemblyLoadContext("official-sdk-nuget", false)
{
    internal static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "NuGet.Packaging", "NuGet.Configuration", "NuGet.Versioning", "NuGet.Common", "NuGet.Frameworks",
        "Newtonsoft.Json", "System.Security.Cryptography.Pkcs", "System.Security.Cryptography.ProtectedData"
    };
    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name is null) throw new InvalidOperationException("Unnamed assembly");
        if (Allowed.Contains(name.Name)) return LoadFromAssemblyPath(Path.Combine(sdk, name.Name + ".dll"));
        // Only the installed runtime's trusted platform assemblies can satisfy other dependencies.
        string runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)! + Path.DirectorySeparatorChar;
        var trusted = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator);
        if (trusted.Any(p => p.StartsWith(runtime, StringComparison.Ordinal) && Path.GetFileNameWithoutExtension(p) == name.Name)) return null;
        throw new InvalidOperationException("Dependency outside official allowlist/runtime: " + name.Name);
    }
}

internal static class Program
{
    private static string Sha256(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }
    private static void Save(string path, Dictionary<string, object?> report)
    {
        var temp = path + ".writing";
        File.WriteAllText(temp, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.Move(temp, path, true);
    }
    private static object Identity(Assembly assembly)
    {
        string path = assembly.Location;
        return new { name = assembly.GetName().Name, path, assemblyVersion = assembly.GetName().Version?.ToString(),
            informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            sha256 = Sha256(path), moduleVersionId = assembly.ManifestModule.ModuleVersionId.ToString() };
    }
    public static int Main(string[] args)
    {
        // Fixed positional CLI; all paths are independently constrained by the Python caller.
        if (args.Length != 3) return 64;
        string sdk = Path.GetFullPath(args[0]), package = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
        if (File.Exists(output)) return 65;
        var report = new Dictionary<string, object?> { ["schema"] = 1, ["status"] = "INCOMPLETE", ["sdkDirectory"] = sdk,
            ["packagePath"] = package, ["signatureTrust"] = "NOT_VERIFIED", ["contentHashAlgorithm"] = "SHA512",
            ["contentHashSemantics"] = "NuGet.PackageArchiveReader.GetContentHash; signature-independent package content",
            ["rawHashSemantics"] = "whole nupkg ZIP bytes", ["method"] = "NuGet.Packaging.PackageArchiveReader.GetContentHash(System.Threading.CancellationToken,System.Func<System.String>)",
            ["helper"] = Identity(typeof(Program).Assembly), ["runtime"] = Identity(typeof(object).Assembly) };
        OfficialNuGetContext? context = null;
        try
        {
            Save(output, report);
            if (new DirectoryInfo(sdk).Name != "10.0.114") throw new InvalidOperationException("Unexpected SDK directory");
            using var stream = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 128L * 1024 * 1024) throw new InvalidOperationException("Package too large");
            report["packageBytes"] = stream.Length;
            report["rawSha256"] = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            stream.Position = 0;
            report["rawSha512"] = Convert.ToBase64String(SHA512.HashData(stream));
            stream.Position = 0;
            Save(output, report); // Preserve raw values before any official API failure.
            context = new OfficialNuGetContext(sdk);
            var assembly = context.LoadFromAssemblyPath(Path.Combine(sdk, "NuGet.Packaging.dll"));
            var type = assembly.GetType("NuGet.Packaging.PackageArchiveReader", true)!;
            var constructor = type.GetConstructor(new[] { typeof(Stream), typeof(bool) }) ?? throw new MissingMethodException("PackageArchiveReader(Stream,bool)");
            var method = type.GetMethod("GetContentHash", new[] { typeof(CancellationToken), typeof(Func<string>) }) ?? throw new MissingMethodException("GetContentHash");
            report["methodModuleVersionId"] = method.Module.ModuleVersionId.ToString();
            report["methodMetadataToken"] = method.MetadataToken;
            using var reader = (IDisposable)constructor.Invoke(new object[] { stream, true });
            // Null callback: do not trust legacy sidecar hashes as a substitute for actual bytes.
            string value = (string?)method.Invoke(reader, new object?[] { CancellationToken.None, null }) ?? throw new InvalidDataException("No content hash");
            if (Convert.FromBase64String(value).Length != 64) throw new InvalidDataException("Invalid SHA512 content hash");
            report["contentHash"] = value;
            stream.Position = 0;
            if (!Equals(report["rawSha256"], Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant())) throw new InvalidDataException("Package changed during read");
            report["status"] = "CONTENT_HASH_OBSERVED_NOT_SIGNATURE_VERIFICATION";
        }
        catch (Exception error)
        {
            Exception actual = error is TargetInvocationException { InnerException: not null } tie ? tie.InnerException! : error;
            report["errorType"] = actual.GetType().FullName;
            report["error"] = actual.Message;
        }
        finally
        {
            try { report["loadedAssemblies"] = context?.Assemblies.Select(Identity).ToArray() ?? Array.Empty<object>(); }
            catch (Exception error) { report["status"] = "INCOMPLETE"; report["identityError"] = error.Message; }
            Save(output, report);
        }
        return Equals(report["status"], "CONTENT_HASH_OBSERVED_NOT_SIGNATURE_VERIFICATION") ? 0 : 1;
    }
}
