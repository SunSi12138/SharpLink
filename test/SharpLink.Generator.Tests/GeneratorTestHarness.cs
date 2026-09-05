using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpLink.Generator.Tests;

internal static class GeneratorTestHarness
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> PlatformReferences =
        new(CreatePlatformReferences);

    internal static CSharpCompilation CreateCompilation(
        string assemblyName,
        string source,
        IEnumerable<MetadataReference>? additionalReferences = null)
    {
        var references = PlatformReferences.Value;
        if (additionalReferences is not null)
            references = references.AddRange(additionalReferences);

        return CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    internal static GeneratorDriverRunResult Run(
        string assemblyName,
        string source,
        IEnumerable<MetadataReference>? additionalReferences = null)
    {
        IIncrementalGenerator generator = new RpcGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        return driver.RunGenerators(CreateCompilation(assemblyName, source, additionalReferences)).GetRunResult();
    }

    internal static ImmutableArray<MetadataReference> GetPlatformReferences()
        => PlatformReferences.Value;

    private static ImmutableArray<MetadataReference> CreatePlatformReferences()
    {
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (string.IsNullOrWhiteSpace(tpa))
            throw new InvalidOperationException("TRUSTED_PLATFORM_ASSEMBLIES is unavailable.");

        return tpa.Split(Path.PathSeparator)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToImmutableArray();
    }
}
