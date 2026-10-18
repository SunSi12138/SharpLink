using System;
using System.IO;
using Microsoft.CodeAnalysis;

namespace SharpLink.Generator.Tests;

// A real Abstractions reference is supplied only to these full-generation
// compilations. The test process and legacy synthetic-SDK Roslyn fixtures must
// NOT reference it, since many fixtures deliberately redefine those types.
internal static class RealAbstractionsCompilationReference
{
    internal static MetadataReference Create()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Sharplink.slnx")))
            root = root.Parent;

        if (root is null)
            throw new InvalidOperationException("The SharpLink source root was not found.");

        // Test output: .../test/SharpLink.Generator.Tests/bin/<Configuration>/net10.0
        var testOutput = new DirectoryInfo(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var configuration = testOutput.Parent?.Name ??
            throw new InvalidOperationException("Generator test build configuration is unavailable.");
        var assembly = Path.Combine(root.FullName, "src", "SharpLink.Abstractions", "bin",
            configuration, "net10.0", "SharpLink.Abstractions.dll");
        if (!File.Exists(assembly))
            throw new FileNotFoundException(
                "Build the real SharpLink.Abstractions project before generated-compilation tests.", assembly);

        return MetadataReference.CreateFromFile(assembly);
    }
}
