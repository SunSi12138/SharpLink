using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 3)
    throw new ArgumentException("Usage: tests <dotnet-host> <scanner-dll> <reader-directory>");
var host = Path.GetFullPath(args[0]);
var scanner = Path.GetFullPath(args[1]);
var root = Path.GetFullPath(args[2]);
var scratch = Path.Combine(root, ".test-output");
Directory.CreateDirectory(scratch);
var checks = new List<string>();
var grants = Path.Combine(root, "tests/fixtures/Grants/bin/Release/net10.0/Grants.dll");
var reference = Path.Combine(root, "tests/fixtures/Grants/obj/Release/net10.0/ref/Grants.dll");
var noGrants = Path.Combine(root, "tests/fixtures/NoGrants/bin/Release/net10.0/NoGrants.dll");

using (var document = Success(grants, noGrants, reference))
{
    var assemblies = document.RootElement.GetProperty("Assemblies");
    Check(assemblies.GetArrayLength() == 3, "Exact requested files, including same basename, preserved");
    var first = assemblies[0];
    var friends = Strings(first, "InternalsVisibleTo");
    Check(friends.SequenceEqual(new[] { "Duplicated.Friend", "Duplicated.Friend",
        "Qualified.Friend, PublicKey=00000000000000000400000000000000" }), "Duplicated and public-key-qualified IVT retained exactly");
    // Roslyn coalesces byte-identical attributes. Explicit PE duplicate rows are
    // tested below, separately from compilation's metadata emission behavior.
    Check(Strings(first, "IgnoresAccessChecksTo").SequenceEqual(new[] { "Fixture.Target" }), "Compiled IgnoresAccessChecksTo decoded");
    var attrs = first.GetProperty("RecognizedAttributes").EnumerateArray().ToArray();
    Check(attrs.Any(a => a.GetProperty("TypeName").GetString()!.EndsWith("InternalsVisibleToAttribute", StringComparison.Ordinal)
        && a.GetProperty("ConstructorKind").GetString() == "MemberReference"), "Compiled MemberRef constructor read");
    Check(attrs.Any(a => a.GetProperty("TypeName").GetString()!.EndsWith("IgnoresAccessChecksToAttribute", StringComparison.Ordinal)
        && a.GetProperty("ConstructorKind").GetString() == "MethodDefinition"), "Compiled MethodDef constructor read");
    Check(attrs.Any(a => a.GetProperty("AllInternalsVisible").ValueKind == JsonValueKind.False), "AllInternalsVisible named property retained");
    Check(first.GetProperty("InformationalVersion").GetString() == "1.2.3+fixture", "Informational version recorded");
    Check(first.GetProperty("Identity").GetProperty("Version").GetString() == "1.2.3.4", "Assembly version recorded");
    Check(Guid.TryParse(first.GetProperty("ModuleVersionId").GetString(), out var mvid) && mvid != Guid.Empty, "MVID recorded");
    Check(first.GetProperty("Sha256").GetString() == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(grants))), "SHA256 binds exact input bytes");
    Check(first.GetProperty("AssemblyReferences").GetArrayLength() > 0, "Compiled AssemblyRefs recorded");
    Check(!first.GetProperty("IsReferenceAssembly").GetBoolean() && assemblies[2].GetProperty("IsReferenceAssembly").GetBoolean(), "Implementation and reference assemblies distinguished");
    Check(Strings(assemblies[1], "InternalsVisibleTo").Length == 0 && Strings(assemblies[1], "IgnoresAccessChecksTo").Length == 0
        && !assemblies[1].GetProperty("IsReferenceAssembly").GetBoolean(), "Other-namespace same-short-name attributes ignored");
    Check(assemblies[1].GetProperty("FileName").GetString() == "NoGrants.dll", "No unspecified files scanned");
}

var synthetic = Emit("member-ref", new byte[] { 1, 0, 6, 70, 114, 105, 101, 110, 100, 0, 0 }, forwarder: true);
using (var document = Success(synthetic))
{
    var assembly = document.RootElement.GetProperty("Assemblies")[0];
    Check(Strings(assembly, "InternalsVisibleTo").Single() == "Friend", "Synthetic MemberRef actual assembly attribute decoded");
    var dependency = assembly.GetProperty("AssemblyReferences").EnumerateArray().Single(a => a.GetProperty("Name").GetString() == "Fixture.Dependency");
    Check(dependency.GetProperty("FullName").GetString() == "Fixture.Dependency, Version=2.3.4.5, Culture=fr-FR, PublicKeyToken=0102030405060708, Retargetable=Yes, ContentType=WindowsRuntime", "AssemblyRef full identity preserves version, culture, token and flags");
    var fullKey = assembly.GetProperty("AssemblyReferences").EnumerateArray().Single(a => a.GetProperty("Name").GetString() == "Fixture.FullKey");
    Check(fullKey.GetProperty("HasFullPublicKey").GetBoolean()
        && fullKey.GetProperty("PublicKeyOrToken").GetString() == "00000000000000000400000000000000"
        && fullKey.GetProperty("FullName").GetString() == "Fixture.FullKey, Version=1.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089", "AssemblyRef full public key retained and canonical token derived");
    var forward = assembly.GetProperty("TypeForwarders")[0];
    Check(forward.GetProperty("TypeName").GetString() == "Forwarded.Marker"
        && forward.GetProperty("Target").GetProperty("FullName").GetString() == dependency.GetProperty("FullName").GetString(), "ExportedType forwarder binds full AssemblyRef identity");
    Check(assembly.GetProperty("TypeForwarders").EnumerateArray().Select(v => v.GetProperty("TypeName").GetString()).SequenceEqual(
        new[] { "Forwarded.Marker", "Forwarded.Marker+Nested", "Forwarded.Marker+Nested+Deep" }), "Nested ExportedTypes inherit containing forwarder flag");
}

using (var document = Success(Emit("duplicate-ivt", Payload("Exact.Friend"), duplicateCount: 2)))
    Check(Strings(document.RootElement.GetProperty("Assemblies")[0], "InternalsVisibleTo").SequenceEqual(new[] { "Exact.Friend", "Exact.Friend" }), "Byte-identical duplicate IVT metadata rows preserved");
using (var document = Success(Emit("duplicate-access", Payload("Exact.Target"), attributeName: "IgnoresAccessChecksToAttribute", duplicateCount: 2)))
    Check(Strings(document.RootElement.GetProperty("Assemblies")[0], "IgnoresAccessChecksTo").SequenceEqual(new[] { "Exact.Target", "Exact.Target" }), "Byte-identical duplicate IgnoresAccessChecksTo metadata rows preserved");

using (var document = Success(Emit("method-def", Payload("MethodFriend"), methodDef: true)))
    Check(Strings(document.RootElement.GetProperty("Assemblies")[0], "InternalsVisibleTo").Single() == "MethodFriend", "Synthetic MethodDef attribute decoded");

using (var document = Success(Emit("reference-description", Payload("fixture description"),
    attributeName: "ReferenceAssemblyAttribute")))
    Check(document.RootElement.GetProperty("Assemblies")[0].GetProperty("IsReferenceAssembly").GetBoolean(), "ReferenceAssembly string constructor supported");
using (var document = Success(Emit("reference-null-description", [1, 0, 255, 0, 0], attributeName: "ReferenceAssemblyAttribute")))
    Check(document.RootElement.GetProperty("Assemblies")[0].GetProperty("IsReferenceAssembly").GetBoolean(), "ReferenceAssembly null description supported");

using (var document = Success(Emit("non-assembly", Payload("NotAssemblyGrant"), onModule: true)))
    Check(Strings(document.RootElement.GetProperty("Assemblies")[0], "InternalsVisibleTo").Length == 0, "Non-assembly attributes excluded");

using (var document = Success(Emit("nested-imposter", Payload("NestedFake"), nested: true)))
    Check(Strings(document.RootElement.GetProperty("Assemblies")[0], "InternalsVisibleTo").Length == 0, "Nested same-full-name-looking attribute excluded");

Failure(2, "Empty CLI rejected");
Failure(2, "Unknown option rejected", "--unknown");
Failure(1, "Missing input rejected", Path.Combine(scratch, "missing.dll"));
var notPe = Path.Combine(scratch, "not-pe.dll");
File.WriteAllText(notPe, "not a managed assembly");
Failure(1, "Non-PE input rejected", notPe);
var truncated = Path.Combine(scratch, "truncated.dll");
File.WriteAllBytes(truncated, File.ReadAllBytes(grants)[..128]);
Failure(1, "Truncated PE rejected", truncated);

var malformed = new (string Name, byte[] Bytes)[]
{
    ("missing-payload", []),
    ("bad-prolog", [2, 0, 1, 65, 0, 0]),
    ("truncated-string", [1, 0, 8, 65, 0, 0]),
    ("null-string", [1, 0, 255, 0, 0]),
    ("invalid-utf8", [1, 0, 1, 255, 0, 0]),
    ("missing-named-count", [1, 0, 1, 65]),
    ("truncated-named-arg", [1, 0, 1, 65, 1, 0]),
    ("trailing-payload", [1, 0, 1, 65, 0, 0, 42]),
    ("unknown-named-property", [1, 0, 1, 65, 1, 0, 0x54, 0x02, 1, 88, 1]),
};
foreach (var (name, bytes) in malformed)
    Failure(1, $"Malformed attribute rejected: {name}", Emit(name, bytes));
Failure(1, "Wrong constructor signature rejected", Emit("bad-signature", Payload("Friend"), signature: [0x20, 1, 1, 8]));
Failure(1, "Trailing constructor signature rejected", Emit("trailing-signature", Payload("Friend"), signature: [0x20, 1, 1, 0x0e, 0]));
Failure(1, "Non-constructor attribute member rejected", Emit("not-ctor", Payload("Friend"), constructorName: "NotConstructor"));
Failure(1, "All-or-nothing batch has no partial stdout", grants, notPe);

Console.WriteLine(JsonSerializer.Serialize(new { Passed = checks.Count, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
return 0;

void Check(bool condition, string name)
{
    if (!condition)
        throw new InvalidOperationException("Failed: " + name);
    checks.Add(name);
}

(int Exit, string Output, string Error) Run(params string[] files)
{
    var start = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    start.ArgumentList.Add(scanner);
    foreach (var file in files)
        start.ArgumentList.Add(file);
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(30_000))
    {
        process.Kill(entireProcessTree: true);
        throw new InvalidOperationException("Scanner did not terminate in 30 seconds.");
    }
    return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
}

JsonDocument Success(params string[] files)
{
    var result = Run(files);
    if (result.Exit != 0 || result.Error.Length != 0)
        throw new InvalidOperationException($"Scanner failed: exit={result.Exit}; {result.Error}");
    return JsonDocument.Parse(result.Output);
}

void Failure(int exit, string name, params string[] files)
{
    var result = Run(files);
    Check(result.Exit == exit && result.Output.Length == 0 && result.Error.Length > 0, name);
}

static string[] Strings(JsonElement item, string key) => item.GetProperty(key).EnumerateArray().Select(v => v.GetString()!).ToArray();

static byte[] Payload(string value)
{
    var blob = new BlobBuilder();
    blob.WriteUInt16(1);
    blob.WriteSerializedString(value);
    blob.WriteUInt16(0);
    return blob.ToArray();
}

string Emit(string name, byte[] payload, bool methodDef = false, bool forwarder = false,
    string attributeName = "InternalsVisibleToAttribute", bool onModule = false, bool nested = false,
    byte[]? signature = null, string constructorName = ".ctor", int duplicateCount = 1)
{
    var metadata = new MetadataBuilder();
    var module = metadata.AddModule(0, metadata.GetOrAddString(name + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
    var assembly = metadata.AddAssembly(metadata.GetOrAddString(name), new Version(1, 2, 3, 4), default, default, 0, AssemblyHashAlgorithm.Sha256);
    var runtime = metadata.AddAssemblyReference(metadata.GetOrAddString("System.Runtime"), new Version(10, 0, 0, 0), default, default, 0, default);
    var dependency = metadata.AddAssemblyReference(metadata.GetOrAddString("Fixture.Dependency"), new Version(2, 3, 4, 5),
        metadata.GetOrAddString("fr-FR"), metadata.GetOrAddBlob(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }),
        AssemblyFlags.Retargetable | AssemblyFlags.WindowsRuntime, default);
    metadata.AddAssemblyReference(metadata.GetOrAddString("Fixture.FullKey"), new Version(1, 0, 0, 0), default,
        metadata.GetOrAddBlob(Convert.FromHexString("00000000000000000400000000000000")), AssemblyFlags.PublicKey, default);
    var attrBase = metadata.AddTypeReference(runtime, metadata.GetOrAddString("System"), metadata.GetOrAddString("Attribute"));
    metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"), default,
        MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
    EntityHandle ctor;
    var sig = metadata.GetOrAddBlob(signature ?? new byte[] { 0x20, 1, 1, 0x0e });
    if (methodDef)
    {
        metadata.AddTypeDefinition(TypeAttributes.NotPublic, metadata.GetOrAddString("System.Runtime.CompilerServices"),
            metadata.GetOrAddString(attributeName), attrBase, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        ctor = metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            MethodImplAttributes.Runtime, metadata.GetOrAddString(constructorName), sig, 0, MetadataTokens.ParameterHandle(1));
    }
    else
    {
        EntityHandle scope = runtime;
        if (nested)
            scope = metadata.AddTypeReference(runtime, metadata.GetOrAddString("Other"), metadata.GetOrAddString("Container"));
        var attr = metadata.AddTypeReference(scope, metadata.GetOrAddString("System.Runtime.CompilerServices"), metadata.GetOrAddString(attributeName));
        ctor = metadata.AddMemberReference(attr, metadata.GetOrAddString(constructorName), sig);
    }
    for (var i = 0; i < duplicateCount; i++)
        metadata.AddCustomAttribute(onModule ? module : assembly, ctor, metadata.GetOrAddBlob(payload));
    if (forwarder)
    {
        var parent = metadata.AddExportedType(TypeAttributes.Public | (TypeAttributes)0x00200000, metadata.GetOrAddString("Forwarded"),
            metadata.GetOrAddString("Marker"), dependency, 0);
        var nestedType = metadata.AddExportedType(TypeAttributes.NestedPublic, default, metadata.GetOrAddString("Nested"), parent, 0);
        metadata.AddExportedType(TypeAttributes.NestedPublic, default, metadata.GetOrAddString("Deep"), nestedType, 0);
    }
    var pe = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
        new MetadataRootBuilder(metadata), new BlobBuilder());
    var image = new BlobBuilder();
    pe.Serialize(image);
    var path = Path.Combine(scratch, name + ".dll");
    File.WriteAllBytes(path, image.ToArray());
    return path;
}
