using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
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
    Console.WriteLine($"{assembly.GetName().Name}: {output}");
}
