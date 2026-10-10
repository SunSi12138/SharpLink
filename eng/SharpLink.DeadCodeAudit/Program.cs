#pragma warning disable CS0618 // WorkspaceFailed is used for broad Roslyn SDK compatibility; diagnostics are still captured.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

// Run against the real solution via Roslyn/MSBuild.  This is a reference index,
// not a proof of runtime reachability (reflection, external apps, DI, etc.).
if (args.Length > 0 && string.Equals(args[0], "--probe-remove", StringComparison.Ordinal))
    return RemovalProbe.Run(
        args.Length > 1 ? args[1] : Directory.GetCurrentDirectory(),
        args.Length > 2 ? args[2] : Path.Combine(Directory.GetCurrentDirectory(), "artifacts/issue-801-audit"));

var root = Path.GetFullPath(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());
var output = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(root, "artifacts/issue-801-audit"));
Directory.CreateDirectory(output);
var messages = new List<string>();
var msbuildLocator = Type.GetType("Microsoft.Build.Locator.MSBuildLocator, Microsoft.Build.Locator");
if (msbuildLocator is not null)
    msbuildLocator.GetMethod("RegisterDefaults", Type.EmptyTypes)?.Invoke(null, null);
else messages.Add("MSBuildLocator unavailable; workspace might not load.");

var stopwatch = Stopwatch.StartNew();
using var workspace = MSBuildWorkspace.Create(new Dictionary<string, string> { ["Configuration"] = "Release" });
workspace.WorkspaceFailed += (_, e) =>
{
    var diagnostic = e.Diagnostic.ToString();
    messages.Add(diagnostic);
    if (messages.Count < 120) Console.Error.WriteLine("WORKSPACE: " + diagnostic);
};
Solution solution;
try { solution = await workspace.OpenSolutionAsync(Path.Combine(root, "Sharplink.slnx")); }
catch (Exception e)
{
    File.WriteAllText(Path.Combine(output, "failure.txt"), string.Join(Environment.NewLine, messages) + "\n" + e);
    throw;
}

var projects = solution.Projects.OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
var symbols = new Dictionary<string, SymbolRecord>(StringComparer.Ordinal);
var documents = new List<(Document Document, string Path, string Scope)>();
var projectStats = new List<ProjectStat>();

foreach (var project in projects)
{
    var normal = project.Documents.ToArray();
    var generated = new List<Document>();
    try { generated.AddRange(await project.GetSourceGeneratedDocumentsAsync()); }
    catch (Exception e) { messages.Add("GENERATED " + project.Name + ": " + e.Message); }
    projectStats.Add(new ProjectStat(project.Name, normal.Length, generated.Count));
    foreach (var document in normal)
    {
        var path = Relative(root, document.FilePath ?? project.Name + "/" + document.Name);
        documents.Add((document, path, Scope(path)));
    }
    // Source Generator output belongs to the consuming project. A generated
    // file from a test/demo project is NOT proof of production reachability.
    var projectScope = Scope(Relative(root, project.FilePath ?? project.Name));
    var generatedScope = projectScope == "production" ? "generated"
        : projectScope is "tests" or "benchmarks" or "examples" ? projectScope : "other";
    foreach (var document in generated)
        documents.Add((document, "<generated>/" + project.Name + "/" + document.Name, generatedScope));
    Console.WriteLine($"PROJECT {project.Name}: {normal.Length} source docs; {generated.Count} generated docs");
}

// Index all production-side declarations, including nested and private types.
var indexed = 0;
foreach (var (document, path, scope) in documents)
{
    if (scope != "production") continue;
    try
    {
        var syntax = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();
        if (syntax is null || model is null) continue;
        foreach (var node in syntax.DescendantNodes().Where(IsDeclaration))
        {
            ISymbol? symbol;
            try { symbol = model.GetDeclaredSymbol(node); } catch { continue; }
            if (symbol is null || symbol.IsImplicitlyDeclared) continue;
            var key = Key(symbol);
            if (symbols.ContainsKey(key)) continue;
            symbols.Add(key, new SymbolRecord
            {
                Key = key,
                Name = symbol.Name,
                Signature = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                Kind = symbol.Kind.ToString(),
                Visibility = symbol.DeclaredAccessibility.ToString(),
                ExternallyVisible = ExternallyVisible(symbol),
                ProtectedRoot = ProtectionReason(symbol),
                Assembly = symbol.ContainingAssembly?.Identity.Name ?? "",
                Path = path,
                Line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            });
        }
        indexed++;
    }
    catch (Exception e) { messages.Add("DECLARATION " + path + ": " + e.Message); }
}
Console.WriteLine($"DECLARATIONS: {symbols.Count} indexed from {indexed} documents after {stopwatch.Elapsed}");

// One pass over actual compilations and generated documents; semantic SymbolInfo
// distinguishes overloads, generic methods, extension dispatch and constructors.
// Linked source files and C# 14 extension-block helper symbols sometimes
// surface from MSBuildWorkspace under a distinct emitted/containing symbol.
// Fall back to their declared source location and name (not name alone).
var declarationsBySource = symbols.Values
    .GroupBy(v => v.Path + "|" + v.Name, StringComparer.Ordinal)
    .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
var fallbackMatches = 0L;
var referenceMatches = 0L;
foreach (var (document, path, scope) in documents)
{
    try
    {
        var syntax = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();
        if (syntax is null || model is null) continue;
        foreach (var node in syntax.DescendantNodes().Where(IsReference))
        {
            ISymbol? referenced;
            try { referenced = model.GetSymbolInfo(node).Symbol; } catch { continue; }
            if (referenced is null) continue;
            if (!symbols.TryGetValue(Key(referenced), out var item))
            {
                var sourceLocation = referenced.Locations.FirstOrDefault(l => l.IsInSource);
                var sourcePath = sourceLocation?.SourceTree?.FilePath;
                if (sourceLocation is null || sourcePath is null
                    || !declarationsBySource.TryGetValue(
                        Relative(root, sourcePath) + "|" + referenced.Name, out var options)
                    || options.Length == 0)
                    continue;
                // The location points to the declaration identifier; the record
                // starts at the declaration's first token, which may be earlier.
                var declarationLine = sourceLocation.GetLineSpan().StartLinePosition.Line + 1;
                item = options.OrderBy(v => Math.Abs(v.Line - declarationLine)).First();
                fallbackMatches++;
            }
            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            if (!item.Seen.Add(scope + "|" + path + "|" + line)) continue;
            switch (scope)
            {
                case "production": item.Production++; break;
                case "generated": item.Generated++; break;
                case "tests": item.Tests++; break;
                case "benchmarks": item.Benchmarks++; break;
                case "examples": item.Examples++; break;
                default: item.Other++; break;
            }
            if (item.SampleReferences.Count < 12)
                item.SampleReferences.Add(new Reference(scope, path, line));
            referenceMatches++;
        }
    }
    catch (Exception e) { messages.Add("REFERENCE " + path + ": " + e.Message); }
}
Console.WriteLine($"REFERENCES: {referenceMatches} matched ({fallbackMatches} source-location fallbacks) after {stopwatch.Elapsed}");

var sourceFiles = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
    .Where(p => !p.Replace('\\','/').Contains("/obj/") && !p.Replace('\\','/').Contains("/bin/"))
    .Select(p => Relative(root, p)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
var sourceFileSet = sourceFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
var loadedFiles = documents.Where(d => d.Scope == "production")
    .Select(d => d.Path).Where(sourceFileSet.Contains)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
var missingFiles = sourceFiles.Where(p => !loadedFiles.Contains(p)).ToArray();
foreach (var record in symbols.Values)
{
    record.Tier = record.Production + record.Generated > 0 ? "D-REFERENCED-PRODUCTION"
        : record.ExternallyVisible ? "C-PUBLIC-API-REVIEW"
        : record.Tests + record.Benchmarks + record.Examples > 0 ? "B-ONLY-TEST-BENCH-EXAMPLE"
        : "A-NO-DIRECT-REFERENCE";
    record.Seen.Clear();
}
var sorted = symbols.Values.OrderBy(x => x.Tier).ThenBy(x => x.Path).ThenBy(x => x.Line).ToArray();
var result = new
{
    BaselineDevSha = Environment.GetEnvironmentVariable("AUDIT_DEV_SHA") ?? "unknown",
    AuditHeadSha = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "local",
    ElapsedSeconds = (int)stopwatch.Elapsed.TotalSeconds,
    Projects = projectStats,
    TotalSourceDocuments = documents.Count(d => !d.Path.StartsWith("<generated>/", StringComparison.Ordinal)),
    TotalGeneratedDocuments = documents.Count(d => d.Path.StartsWith("<generated>/", StringComparison.Ordinal)),
    TotalProductionFiles = sourceFiles.Length,
    IndexedProductionFiles = loadedFiles.Count,
    MissingProductionFiles = missingFiles,
    Declarations = sorted.Length,
    MatchedReferences = referenceMatches,
    SourceLocationFallbackMatches = fallbackMatches,
    Breakdown = sorted.GroupBy(x => x.Tier).ToDictionary(g => g.Key, g => g.Count()),
    WorkspaceDiagnostics = messages.Take(400).ToArray(),
    Symbols = sorted,
};
File.WriteAllText(Path.Combine(output, "report.json"),
    JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

foreach (var group in sorted.GroupBy(r => r.Tier))
{
    var csv = new StringBuilder("path,line,assembly,kind,visibility,signature,production,generated,tests,benchmarks,examples,root\n");
    foreach (var item in group)
    {
        string quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        csv.AppendLine(string.Join(',', new[] {
            quote(item.Path), item.Line.ToString(), quote(item.Assembly), quote(item.Kind),
            quote(item.Visibility), quote(item.Signature), item.Production.ToString(),
            item.Generated.ToString(), item.Tests.ToString(), item.Benchmarks.ToString(),
            item.Examples.ToString(), quote(item.ProtectedRoot)
        }));
    }
    File.WriteAllText(Path.Combine(output, "tier-" + group.Key.Split('-')[0] + ".csv"), csv.ToString());
}
var markdown = new StringBuilder();
markdown.AppendLine("# Issue #801 — dev full-solution symbol reference index");
markdown.AppendLine();
markdown.AppendLine("Dev base: " + (Environment.GetEnvironmentVariable("AUDIT_DEV_SHA") ?? "unknown"));
markdown.AppendLine("Audit head: " + (Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "unknown"));
markdown.AppendLine($"Projects {projects.Length}; source docs {documents.Count(d => !d.Path.StartsWith("<generated>/", StringComparison.Ordinal))}; generated docs {documents.Count(d => d.Path.StartsWith("<generated>/", StringComparison.Ordinal))}");
markdown.AppendLine($"Production source files indexed: {loadedFiles.Count}/{sourceFiles.Length}; declarations: {sorted.Length}; matched references: {referenceMatches}; elapsed seconds: {(int)stopwatch.Elapsed.TotalSeconds}");
markdown.AppendLine("**Caution:** A/B/C are review candidates, NOT proof of dead code. This scans direct semantic references, not whole-program reachability, reflection, DI or external consumers.");
markdown.AppendLine();
markdown.AppendLine("## Tier counts");
foreach (var group in sorted.GroupBy(x => x.Tier))
    markdown.AppendLine("- " + group.Key + ": " + group.Count());
markdown.AppendLine("## Unindexed production files");
foreach (var item in missingFiles.Take(100)) markdown.AppendLine("- " + item);
markdown.AppendLine("## Workspace diagnostics (first 30)");
foreach (var item in messages.Take(30))
    markdown.AppendLine("- " + item.Replace("\r", " ").Replace("\n", " "));
foreach (var group in sorted.Where(x => x.Tier.StartsWith("A-") || x.Tier.StartsWith("B-") || x.Tier.StartsWith("C-"))
    .GroupBy(x => x.Tier))
{
    markdown.AppendLine();
    markdown.AppendLine("## " + group.Key + " (first 120)");
    markdown.AppendLine("| Symbol | Source | Production | Generated | Tests | Benchmarks | Examples | Safety root |");
    markdown.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |");
    foreach (var item in group.OrderBy(x => x.ProtectedRoot.Length != 0)
        .ThenBy(x => x.Path).ThenBy(x => x.Line).Take(120))
        markdown.AppendLine("| " + item.Signature.Replace("|", "\\|") + " | " +
            item.Path + ":" + item.Line + " | " + item.Production + " | " +
            item.Generated + " | " + item.Tests + " | " + item.Benchmarks +
            " | " + item.Examples + " | " + (item.ProtectedRoot.Length > 0 ? item.ProtectedRoot : "-") + " |");
}
File.WriteAllText(Path.Combine(output, "report.md"), markdown.ToString());
Console.WriteLine("=== ISSUE 801 AUDIT SUMMARY ===");
Console.WriteLine($"Indexed source files: {loadedFiles.Count}/{sourceFiles.Length}; symbols: {sorted.Length}; generated docs: {documents.Count(d=>d.Path.StartsWith("<generated>/", StringComparison.Ordinal))}");
foreach (var group in sorted.GroupBy(x=>x.Tier)) Console.WriteLine(group.Key + ": " + group.Count());
Console.WriteLine("Full evidence: " + output);
return 0;

static bool IsDeclaration(SyntaxNode n) =>
    n is BaseMethodDeclarationSyntax or BaseTypeDeclarationSyntax or DelegateDeclarationSyntax
        or PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax
        or EnumMemberDeclarationSyntax
    || n is VariableDeclaratorSyntax v &&
        v.Parent?.Parent is FieldDeclarationSyntax or EventFieldDeclarationSyntax;

static bool IsReference(SyntaxNode n) => n is SimpleNameSyntax or InvocationExpressionSyntax
    or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax
    or AttributeSyntax or ElementAccessExpressionSyntax;

static ISymbol Normalize(ISymbol symbol)
{
    if (symbol is IAliasSymbol alias) symbol = alias.Target;
    if (symbol is IMethodSymbol method)
    {
        symbol = method.ReducedFrom ?? method;
        if (symbol is IMethodSymbol m && m.AssociatedSymbol is { } associated)
            symbol = associated;
    }
    return symbol.OriginalDefinition;
}
static string Key(ISymbol symbol)
{
    var canonical = Normalize(symbol);
    return (canonical.ContainingAssembly?.Identity.Name ?? "<none>") + "|" +
        canonical.Kind + "|" + canonical.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
}
static bool ExternallyVisible(ISymbol symbol)
{
    for (ISymbol? current = symbol; current is not null; current = current.ContainingType)
    {
        if (current.DeclaredAccessibility is Accessibility.Private or Accessibility.Internal
            or Accessibility.ProtectedAndInternal) return false;
    }
    return true;
}
static string ProtectionReason(ISymbol symbol)
{
    if (symbol.ContainingType?.TypeKind == TypeKind.Interface) return "Interface contract";
    // Implicit interface implementations have no special syntax and are often
    // reached exclusively through an interface-valued variable or framework callback.
    if (symbol.ContainingType is INamedTypeSymbol type && type.AllInterfaces.Length > 0)
    {
        foreach (var iface in type.AllInterfaces)
        {
            foreach (var member in iface.GetMembers())
            {
                var impl = type.FindImplementationForInterfaceMember(member);
                if (impl is not null && SymbolEqualityComparer.Default.Equals(
                        impl.OriginalDefinition, symbol.OriginalDefinition))
                    return "Implicit interface implementation / framework dispatch";
            }
        }
    }
    if (symbol.ContainingType?.TypeKind == TypeKind.Enum) return "Enum/protocol identity";
    if (symbol is IMethodSymbol method)
    {
        if (method.IsOverride || method.IsVirtual || method.IsAbstract) return "Virtual dispatch";
        if (method.ExplicitInterfaceImplementations.Length > 0) return "Interface implementation";
        if (method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor)
            return "Constructor/activation";
        if (method.Name is "Main" or "Dispose" or "DisposeAsync") return "Entrypoint/lifecycle";
    }
    if (symbol is IPropertySymbol prop)
    {
        if (prop.IsOverride || prop.IsVirtual || prop.IsAbstract) return "Virtual dispatch";
        if (prop.ExplicitInterfaceImplementations.Length > 0) return "Interface implementation";
    }
    if (symbol.GetAttributes().Any(a => a.AttributeClass?.Name is
        "UnmanagedCallersOnlyAttribute" or "ModuleInitializerAttribute"))
        return "Attribute-discovered entrypoint";
    return "";
}
static string Relative(string root, string name)
{
    var path = Path.IsPathRooted(name) ? Path.GetRelativePath(root, name) : name;
    return path.Replace('\\','/');
}
static string Scope(string path)
{
    if (path.StartsWith("src/",StringComparison.OrdinalIgnoreCase)) return "production";
    if (path.StartsWith("test/",StringComparison.OrdinalIgnoreCase))
        return path.Contains("Benchmarks",StringComparison.OrdinalIgnoreCase) ? "benchmarks" : "tests";
    if (path.StartsWith("demo/",StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("samples/",StringComparison.OrdinalIgnoreCase)) return "examples";
    return "other";
}
sealed class SymbolRecord
{
    public string Key {get;set;} = "";
    public string Name {get;set;} = "";
    public string Signature {get;set;} = "";
    public string Kind {get;set;} = "";
    public string Visibility {get;set;} = "";
    public string Assembly {get;set;} = "";
    public string Path {get;set;} = "";
    public int Line {get;set;}
    public string Tier {get;set;} = "";
    public string ProtectedRoot {get;set;} = "";
    public bool ExternallyVisible {get;set;}
    public int Production {get;set;}
    public int Generated {get;set;}
    public int Tests {get;set;}
    public int Benchmarks {get;set;}
    public int Examples {get;set;}
    public int Other {get;set;}
    public List<Reference> SampleReferences {get;set;} = new();
    [System.Text.Json.Serialization.JsonIgnore]
    public HashSet<string> Seen {get;} = new(StringComparer.Ordinal);
}
sealed record Reference(string Scope, string Path, int Line);
sealed record ProjectStat(string Project, int SourceDocuments, int GeneratedDocuments);
