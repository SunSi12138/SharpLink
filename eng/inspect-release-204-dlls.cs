using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length != 3 || !Regex.IsMatch(args[1], "^[0-9a-f]{40}$"))
    throw new ArgumentException("Expected source directory, exact SHA, and output JSON.");
var root = Path.GetFullPath(args[0]);
var identities = new List<Identity>();
foreach (var tree in new[] { "src", "test" })
foreach (var path in Directory.EnumerateFiles(Path.Combine(root, tree), "SharpLink*.dll", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
{
    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
    if (!relative.Contains("/bin/Release/", StringComparison.Ordinal))
        continue;
    // Hash and inspect the same immutable byte snapshot; never load or execute the assembly.
    var bytes = File.ReadAllBytes(path);
    using var pe = new PEReader(new MemoryStream(bytes, writable: false));
    if (!pe.HasMetadata)
        throw new BadImageFormatException("Missing managed assembly metadata: " + relative);
    var metadata = pe.GetMetadataReader();
    var assembly = metadata.GetAssemblyDefinition();
    var versions = new List<string>();
    foreach (var handle in assembly.GetCustomAttributes())
    {
        var attribute = metadata.GetCustomAttribute(handle);
        EntityHandle type = attribute.Constructor.Kind switch
        {
            HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
            HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
            _ => default
        };
        string name = "", ns = "";
        if (type.Kind == HandleKind.TypeReference)
        {
            var reference = metadata.GetTypeReference((TypeReferenceHandle)type);
            name = metadata.GetString(reference.Name);
            ns = metadata.GetString(reference.Namespace);
        }
        else if (type.Kind == HandleKind.TypeDefinition)
        {
            var definition = metadata.GetTypeDefinition((TypeDefinitionHandle)type);
            name = metadata.GetString(definition.Name);
            ns = metadata.GetString(definition.Namespace);
        }
        if (ns != "System.Reflection" || name != "AssemblyInformationalVersionAttribute")
            continue;
        var blob = metadata.GetBlobReader(attribute.Value);
        if (blob.ReadUInt16() != 1)
            throw new BadImageFormatException("Invalid attribute prolog: " + relative);
        versions.Add(blob.ReadSerializedString() ?? throw new BadImageFormatException("Null informational version: " + relative));
    }
    if (versions.Count != 1)
        throw new BadImageFormatException("Exactly one informational version is required: " + relative);
    identities.Add(new Identity(relative, metadata.GetString(assembly.Name), assembly.Version.ToString(),
        versions[0], Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
}
if (identities.Count == 0)
    throw new InvalidDataException("No SharpLink Release DLLs found.");
File.WriteAllText(args[2], JsonSerializer.Serialize(new { SchemaVersion = 1, SourceCommit = args[1], Assemblies = identities },
    new JsonSerializerOptions { WriteIndented = true }) + "\n");

internal sealed record Identity(string Path, string Name, string AssemblyVersion, string InformationalVersion, string Sha256);
