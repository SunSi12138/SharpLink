using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;

if (args.Length != 1 || !File.Exists(args[0]))
    throw new ArgumentException("Pass exactly one packed SharpLink.Sdk .nupkg path.");

var packagePath = Path.GetFullPath(args[0]);
var outputDirectory = Path.Combine(Path.GetDirectoryName(packagePath)!, ".codefix-host-smoke");
Directory.CreateDirectory(outputDirectory);
var assemblyPath = Path.Combine(outputDirectory, "SharpLink.CodeFixes.dll");

using (var package = ZipFile.OpenRead(packagePath))
{
    var entry = package.GetEntry("analyzers/dotnet/cs/SharpLink.CodeFixes.dll") ??
        throw new InvalidOperationException("The SDK package does not contain SharpLink.CodeFixes.dll.");
    entry.ExtractToFile(assemblyPath, overwrite: true);
}

var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
var providerTypes = assembly.GetTypes()
    .Where(type => !type.IsAbstract && typeof(CodeFixProvider).IsAssignableFrom(type))
    .ToArray();
if (providerTypes.Length != 1)
    throw new InvalidOperationException($"Expected one CodeFixProvider, found {providerTypes.Length}.");

var provider = (CodeFixProvider?)Activator.CreateInstance(providerTypes[0], nonPublic: true) ??
    throw new InvalidOperationException("Could not instantiate the packed CodeFixProvider.");
if (!provider.FixableDiagnosticIds.Contains("SHARPLINK006", StringComparer.Ordinal))
    throw new InvalidOperationException("The packed provider does not advertise SHARPLINK006.");

using var workspace = new AdhocWorkspace();
if (!workspace.Services.SupportedLanguages.Contains(LanguageNames.CSharp, StringComparer.Ordinal))
    throw new InvalidOperationException("The workspace host does not support C#.");

Console.WriteLine($"Loaded {providerTypes[0].FullName} from the packed SDK in a C# workspace host.");
