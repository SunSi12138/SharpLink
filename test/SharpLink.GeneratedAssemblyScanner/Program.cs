using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace SharpLink.GeneratedAssemblyScanner;

internal static class Program
{
    private const string RuntimeAssemblyName = "SharpLink.Runtime";
    private const string RuntimeNamespace = "SharpLink.Runtime";

    public static int Main(string[] args)
    {
        if (args.Length < 2 || args[0] is not ("--verify-clean" or "--expect-runtime"))
        {
            Console.Error.WriteLine(
                "Usage: SharpLink.GeneratedAssemblyScanner " +
                "<--verify-clean|--expect-runtime> <assembly> [assembly ...]");
            return 2;
        }

        var expectRuntime = string.Equals(args[0], "--expect-runtime", StringComparison.Ordinal);
        var failed = false;
        for (var index = 1; index < args.Length; index++)
        {
            var result = Scan(Path.GetFullPath(args[index]));
            Console.WriteLine(JsonSerializer.Serialize(result));
            var hasRuntimeReference = result.RuntimeAssemblyReferences.Count != 0 ||
                                      result.RuntimeTypeReferences.Count != 0;
            if (hasRuntimeReference != expectRuntime)
            {
                Console.Error.WriteLine(expectRuntime
                    ? $"Expected '{result.Path}' to contain the API 3 Runtime dependency baseline."
                    : $"Generated assembly '{result.Path}' still references SharpLink.Runtime.");
                failed = true;
            }
        }
        return failed ? 1 : 0;
    }

    private static DependencyScanResult Scan(string path)
    {
        using var stream = File.OpenRead(path);
        using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (!peReader.HasMetadata)
            throw new InvalidDataException($"'{path}' is not a managed assembly.");

        var reader = peReader.GetMetadataReader();
        var runtimeAssemblyReferences = new List<string>();
        foreach (var handle in reader.AssemblyReferences)
        {
            var reference = reader.GetAssemblyReference(handle);
            var name = reader.GetString(reference.Name);
            if (string.Equals(name, RuntimeAssemblyName, StringComparison.Ordinal))
                runtimeAssemblyReferences.Add(name);
        }

        var runtimeTypeReferences = new List<string>();
        foreach (var handle in reader.TypeReferences)
        {
            var reference = reader.GetTypeReference(handle);
            var typeNamespace = reader.GetString(reference.Namespace);
            if (!string.Equals(typeNamespace, RuntimeNamespace, StringComparison.Ordinal) &&
                !typeNamespace.StartsWith(RuntimeNamespace + ".", StringComparison.Ordinal))
            {
                continue;
            }
            var name = reader.GetString(reference.Name);
            runtimeTypeReferences.Add(string.IsNullOrEmpty(typeNamespace)
                ? name
                : typeNamespace + "." + name);
        }

        runtimeAssemblyReferences.Sort(StringComparer.Ordinal);
        runtimeTypeReferences.Sort(StringComparer.Ordinal);
        return new DependencyScanResult(path, runtimeAssemblyReferences, runtimeTypeReferences);
    }

    private sealed record DependencyScanResult(
        string Path,
        IReadOnlyList<string> RuntimeAssemblyReferences,
        IReadOnlyList<string> RuntimeTypeReferences);
}
