using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

if (args.Length != 1 || !File.Exists(args[0]))
    throw new ArgumentException("Pass exactly one packed SharpLink.Sdk .nupkg path.");

var packagePath = Path.GetFullPath(args[0]);
var outputDirectory = Path.Combine(Path.GetDirectoryName(packagePath)!, ".codefix-host-smoke");
Directory.CreateDirectory(outputDirectory);
var assemblyPath = Path.Combine(outputDirectory, "SharpLink.CodeFixes.dll");
var generatorAssemblyPath = Path.Combine(outputDirectory, "SharpLink.Generator.dll");

using (var package = ZipFile.OpenRead(packagePath))
{
    ExtractAnalyzer("SharpLink.CodeFixes.dll", assemblyPath);
    ExtractAnalyzer("SharpLink.Generator.dll", generatorAssemblyPath);

    void ExtractAnalyzer(string name, string destination)
    {
        var entry = package.GetEntry("analyzers/dotnet/cs/" + name) ??
            throw new InvalidOperationException($"The SDK package does not contain {name}.");
        entry.ExtractToFile(destination, overwrite: true);
    }
}

AssemblyLoadContext.Default.LoadFromAssemblyPath(generatorAssemblyPath);
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

const string source = """
namespace SharpLink.Sdk
{
    public interface IService { }

    [System.AttributeUsage(System.AttributeTargets.Interface)]
    public sealed class RpcContractAttribute : System.Attribute { }
}

[SharpLink.Sdk.RpcContract]
public interface IContract { }
""";
var projectId = ProjectId.CreateNewId("PackedCodeFixSmoke");
var documentId = DocumentId.CreateNewId(projectId, "Contract.cs");
var projectInfo = ProjectInfo.Create(
    projectId,
    VersionStamp.Create(),
    "PackedCodeFixSmoke",
    "PackedCodeFixSmoke",
    LanguageNames.CSharp,
    parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
    compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
var solution = workspace.CurrentSolution.AddProject(projectInfo)
    .AddMetadataReferences(projectId, GetPlatformReferences())
    .AddDocument(documentId, "Contract.cs", SourceText.From(source));
var document = solution.GetDocument(documentId) ??
    throw new InvalidOperationException("The smoke-test document was unavailable.");
var tree = await document.GetSyntaxTreeAsync() ??
    throw new InvalidOperationException("The smoke-test syntax tree was unavailable.");
var identifierStart = source.IndexOf("IContract", StringComparison.Ordinal);
var descriptor = new DiagnosticDescriptor(
    "SHARPLINK006",
    "SHARPLINK006",
    "Synthetic packed-provider smoke diagnostic",
    "Smoke",
    DiagnosticSeverity.Error,
    isEnabledByDefault: true);
var diagnostic = Diagnostic.Create(
    descriptor,
    Location.Create(tree, new TextSpan(identifierStart, "IContract".Length)));
var actions = new List<CodeAction>();
var context = new CodeFixContext(
    document,
    diagnostic.Location.SourceSpan,
    [diagnostic],
    (action, _) => actions.Add(action),
    CancellationToken.None);
await provider.RegisterCodeFixesAsync(context);
if (!actions.Any(static action => action.EquivalenceKey == "AddIService"))
    throw new InvalidOperationException("The packed provider could not execute its Generator-backed preflight.");

Console.WriteLine(
    $"Loaded and executed {providerTypes[0].FullName} with its Generator dependency from the packed SDK.");

static IEnumerable<MetadataReference> GetPlatformReferences()
{
    var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
    if (string.IsNullOrWhiteSpace(tpa))
        throw new InvalidOperationException("TRUSTED_PLATFORM_ASSEMBLIES is unavailable.");
    return tpa.Split(Path.PathSeparator)
        .Where(static path => !string.IsNullOrWhiteSpace(path))
        .Select(static path => MetadataReference.CreateFromFile(path));
}
