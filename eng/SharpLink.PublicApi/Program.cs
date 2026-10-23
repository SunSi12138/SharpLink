using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Collections.Generic;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using PublicApiGenerator;

if (args.Length != 2)
    throw new ArgumentException("Usage: SharpLink.PublicApi <assembly-directory> <output-directory>");

var assemblyDirectory = Path.GetFullPath(args[0]);
var outputDirectory = Path.GetFullPath(args[1]);
Directory.CreateDirectory(outputDirectory);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = Path.Combine(assemblyDirectory, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};

var assemblies = Directory.GetFiles(assemblyDirectory, "SharpLink.*.dll")
    .OrderBy(static path => path, StringComparer.Ordinal)
    .ToArray();
if (assemblies.Length == 0)
    throw new InvalidOperationException("No SharpLink package assemblies were found.");

foreach (var path in assemblies)
{
    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    var api = assembly.GeneratePublicApi(new ApiGeneratorOptions
    {
        IncludeAssemblyAttributes = false,
        IncludeForwardedTypes = true,
        DenyNamespacePrefixes = [],
        ExcludeAttributes =
        [
            "System.Runtime.CompilerServices.AsyncStateMachineAttribute",
            "System.Runtime.CompilerServices.AsyncIteratorStateMachineAttribute",
            "System.Runtime.CompilerServices.IteratorStateMachineAttribute",
        ],
    });
    var output = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(path) + ".api.txt");
    File.WriteAllText(output, api.ReplaceLineEndings("\n").TrimEnd() + "\n", new UTF8Encoding(false));
    // PublicApiGenerator expands forwarded declarations and can therefore hide a
    // change from ExportedType forwarder to a same-shaped local TypeDefinition.
    // Record the actual package DLL's ExportedType implementation scopes separately.
    var forwarders = GetTypeForwarders(path);
    var forwardingOutput = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(path) + ".forwarders.txt");
    File.WriteAllText(forwardingOutput, string.Join("\n", forwarders) + (forwarders.Count > 0 ? "\n" : ""), new UTF8Encoding(false));
    // Bind the textual inventories to the actual assembly inspected, including
    // its build identity. Cross-release evidence also records the DLL/package hashes.
    var identity = new
    {
        Name = assembly.GetName().Name,
        Version = assembly.GetName().Version?.ToString(),
        FullName = assembly.FullName,
        ModuleVersionId = assembly.ManifestModule.ModuleVersionId.ToString(),
        InformationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    };
    File.WriteAllText(Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(path) + ".assembly.json"),
        JsonSerializer.Serialize(identity, new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
    Console.WriteLine($"{assembly.GetName().Name}: {output}; forwarded types: {forwarders.Count}");
}

static IReadOnlyList<string> GetTypeForwarders(string assemblyPath)
{
    using var file = File.OpenRead(assemblyPath);
    using var pe = new PEReader(file);
    var metadata = pe.GetMetadataReader();
    var results = new List<string>();
    foreach (var handle in metadata.ExportedTypes)
    {
        var exported = metadata.GetExportedType(handle);
        if (!exported.IsForwarder)
            continue;

        var name = metadata.GetString(exported.Name);
        var ns = metadata.GetString(exported.Namespace);
        var scope = exported.Implementation;
        // ExportedType entries for nested types refer to their containing type.
        // Follow the scope chain to get the actual target AssemblyRef, not the
        // final type's reflection identity after all forwarding has occurred.
        while (scope.Kind == HandleKind.ExportedType)
        {
            var containing = metadata.GetExportedType((ExportedTypeHandle)scope);
            name = metadata.GetString(containing.Name) + "+" + name;
            var containingNamespace = metadata.GetString(containing.Namespace);
            if (!string.IsNullOrEmpty(containingNamespace))
                ns = containingNamespace;
            scope = containing.Implementation;
        }
        if (scope.Kind != HandleKind.AssemblyReference)
            throw new InvalidOperationException($"Unsupported forwarding scope {scope.Kind} in {assemblyPath}.");
        var targetAssembly = metadata.GetAssemblyReference((AssemblyReferenceHandle)scope);
        var typeName = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        results.Add(typeName + " -> " + metadata.GetString(targetAssembly.Name));
    }
    return results.OrderBy(static line => line, StringComparer.Ordinal).ToArray();
}
