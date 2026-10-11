using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

namespace SharpLink.SemanticAudit;

internal static class Program
{
    private sealed class Record
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public bool StaticField { get; set; }
        public bool EnumConstant { get; set; }
        public string Path { get; set; } = "";
        public int Line { get; set; }
        public string Assembly { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Access { get; set; } = "";
        public string Signature { get; set; } = "";
        public string RootRisk { get; set; } = "";
        public string Tier { get; set; } = "";
        public int Production { get; set; }
        public int Generated { get; set; }
        public int Tests { get; set; }
        public int Benchmarks { get; set; }
        public int Examples { get; set; }
        public string ReferenceSamples { get; set; } = "";
    }
    private sealed class Uses
    {
        internal int Production, Generated, Tests, Benchmarks, Examples;
        internal readonly List<string> Samples = new();
        internal void Add(string kind, string path, int line)
        {
            switch (kind)
            {
                case "production": Production++; break;
                case "generated": Generated++; break;
                case "tests": Tests++; break;
                case "benchmarks": Benchmarks++; break;
                default: Examples++; break;
            }
            if (Samples.Count < 3)
                Samples.Add(kind + ":" + path + ":" + line);
        }
    }

    private static async Task<int> Main(string[] args)
    {
        try { return await RunAsync(args).ConfigureAwait(false); }
        catch (Exception exception)
        {
            Console.Error.WriteLine("SEMANTIC AUDIT FAILED: " + exception);
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2)
            throw new ArgumentException("Use: SharpLink.SemanticAudit <repository-root> <output-directory>");
        string root = Path.GetFullPath(args[0]);
        string output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        var stopwatch = Stopwatch.StartNew();
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();

        var workspaceErrors = new List<string>();
        using var workspace = MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(eventArgs =>
        {
            lock (workspaceErrors)
                workspaceErrors.Add(eventArgs.Diagnostic.Kind + ": " + eventArgs.Diagnostic.Message);
        });
        var solution = await workspace.OpenSolutionAsync(Path.Combine(root, "Sharplink.slnx"));
        var projects = solution.Projects.Where(p => p.Language == LanguageNames.CSharp).ToArray();
        var symbols = new Dictionary<string, Record>(StringComparer.Ordinal);
        var references = new Dictionary<string, Uses>(StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        var errors = new List<string>();
        int sourceDocs = 0, generatedDocs = 0, allDocs = 0;

        foreach (var project in projects.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            Console.WriteLine("Index project: " + project.Name);
            try
            {
                if (await project.GetCompilationAsync() is null)
                {
                    errors.Add(project.Name + ": compilation unavailable");
                    continue;
                }
                foreach (var document in project.Documents)
                {
                    string path = Relative(root, document.FilePath ?? document.Name);
                    if (IsBuildOutput(path)) continue;
                    string category = ClassifyPath(path);
                    try
                    {
                        var model = await document.GetSemanticModelAsync();
                        var syntax = await document.GetSyntaxRootAsync();
                        if (model is null || syntax is null)
                        {
                            errors.Add(project.Name + "/" + path + ": no semantic model");
                            continue;
                        }
                        Collect(model, syntax, path, category, category == "production",
                            project.Name, symbols, references);
                        allDocs++;
                        if (category == "production")
                        {
                            covered.Add(path);
                            sourceDocs++;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add(project.Name + "/" + path + ": " + ex);
                    }
                }
                var generated = await project.GetSourceGeneratedDocumentsAsync();
                foreach (var document in generated)
                {
                    try
                    {
                        var model = await document.GetSemanticModelAsync();
                        var syntax = await document.GetSyntaxRootAsync();
                        if (model is null || syntax is null)
                        {
                            errors.Add(project.Name + "/" + document.Name + ": no generated semantic model");
                            continue;
                        }
                        Collect(model, syntax, "generated/" + project.Name + "/" + document.Name,
                            "generated", false, project.Name, symbols, references);
                        generatedDocs++;
                        allDocs++;
                    }
                    catch (Exception ex) { errors.Add(project.Name + "/generated: " + ex); }
                }
            }
            catch (Exception ex) { errors.Add(project.Name + ": " + ex); }
        }

        var physical = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs",
                SearchOption.AllDirectories)
            .Select(path => Relative(root, path)).Where(path => !IsBuildOutput(path))
            .ToHashSet(StringComparer.Ordinal);
        var missing = physical.Except(covered, StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        foreach (var path in missing) errors.Add("Unindexed production source: " + path);

        foreach (var symbol in symbols.Values)
        {
            if (references.TryGetValue(symbol.Key, out var use))
            {
                symbol.Production = use.Production;
                symbol.Generated = use.Generated;
                symbol.Tests = use.Tests;
                symbol.Benchmarks = use.Benchmarks;
                symbol.Examples = use.Examples;
                symbol.ReferenceSamples = string.Join("; ", use.Samples);
            }
            symbol.Tier = symbol.Production + symbol.Generated > 0 ? "D" :
                symbol.Tests + symbol.Benchmarks + symbol.Examples > 0 ? "B" :
                symbol.Access is "Public" or "Protected" or "ProtectedOrInternal" or "ProtectedAndInternal"
                    ? "C" : "A";
        }
        var ordered = symbols.Values.OrderBy(r => r.Tier, StringComparer.Ordinal)
            .ThenBy(r => r.Path, StringComparer.Ordinal).ThenBy(r => r.Line).ToArray();
        WriteCsv(Path.Combine(output, "report.csv"), ordered);
        foreach (var tier in new[] { "A", "B", "C", "D" })
            WriteCsv(Path.Combine(output, "tier-" + tier + ".csv"),
                ordered.Where(r => r.Tier == tier));
        // Match the actual FIELD NAME, not a fully-qualified type name. "RateState",
        // "latest" and "RemoteStreamComplete" otherwise create false test-field positives.
        // Enum constants are not per-instance/static reference slots.
        var residentFields = ordered.Where(r => r.Kind == "Field" && !r.EnumConstant &&
                (r.Path.StartsWith("src/SharpLink.Client/", StringComparison.Ordinal) ||
                 r.Path.StartsWith("src/SharpLink.Server/", StringComparison.Ordinal)) &&
                (r.Name.Contains("Hook", StringComparison.OrdinalIgnoreCase) ||
                 r.Name.Contains("Probe", StringComparison.OrdinalIgnoreCase) ||
                 r.Name.Contains("ForTest", StringComparison.OrdinalIgnoreCase) ||
                 r.Name.StartsWith("_test", StringComparison.OrdinalIgnoreCase) ||
                 r.Name.StartsWith("s_test", StringComparison.OrdinalIgnoreCase))).ToArray();
        WriteCsv(Path.Combine(output, "client-server-resident-test-fields.csv"), residentFields);

        var counts = ordered.GroupBy(r => r.Tier).ToDictionary(g => g.Key, g => g.Count());
        bool complete = errors.Count == 0 && physical.Count == covered.Count && generatedDocs > 0;
        var summary = new
        {
            sourceDevSha = Environment.GetEnvironmentVariable("AUDIT_SOURCE_DEV_SHA") ?? "unknown",
            checkoutSha = Environment.GetEnvironmentVariable("AUDIT_CHECKOUT_SHA") ?? "unknown",
            completedUtc = DateTimeOffset.UtcNow,
            projects = projects.Length,
            sourceFilesExpected = physical.Count,
            sourceFilesIndexed = covered.Count,
            sourceDocuments = sourceDocs,
            generatedDocuments = generatedDocs,
            totalDocuments = allDocs,
            declarations = ordered.Length,
            semanticReferencesObserved = references.Values.Sum(r =>
                (long)r.Production + r.Generated + r.Tests + r.Benchmarks + r.Examples),
            tierA = counts.GetValueOrDefault("A"),
            tierB = counts.GetValueOrDefault("B"),
            tierC = counts.GetValueOrDefault("C"),
            tierD = counts.GetValueOrDefault("D"),
            possibleResidentTestFields = residentFields.Length,
            elapsedSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1),
            complete,
            warning = "Direct semantic reference index only. A/B/C do NOT establish safe dead code. " +
                      "Inspect reflection, DI, source generator roots, NativeAOT, external public API and implicit interface dispatch."
        };
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"),
            JsonSerializer.Serialize(new { summary, symbols = ordered }));
        await File.WriteAllTextAsync(Path.Combine(output, "coverage.json"),
            JsonSerializer.Serialize(new { summary, workspaceErrors, errors, missing },
                new JsonSerializerOptions { WriteIndented = true }));
        var md = new StringBuilder()
            .AppendLine("# Issue #801: exact-dev semantic reference index")
            .AppendLine()
            .AppendLine("Dev SHA: " + summary.sourceDevSha)
            .AppendLine("Workspace checkout: " + summary.checkoutSha)
            .AppendLine("Coverage complete: " + summary.complete)
            .AppendLine("Projects: " + summary.projects)
            .AppendLine("Production source files: " + summary.sourceFilesIndexed + "/" + summary.sourceFilesExpected)
            .AppendLine("Generated documents: " + summary.generatedDocuments)
            .AppendLine("Declarations: " + summary.declarations)
            .AppendLine("Semantic references observed: " + summary.semanticReferencesObserved)
            .AppendLine("Candidate test-resident fields: " + summary.possibleResidentTestFields)
            .AppendLine("Elapsed seconds: " + summary.elapsedSeconds)
            .AppendLine().AppendLine("| Tier | Count |")
            .AppendLine("| --- | ---: |");
        foreach (var tier in new[] { "A", "B", "C", "D" })
            md.AppendLine("| " + tier + " | " + counts.GetValueOrDefault(tier) + " |");
        md.AppendLine().AppendLine("Unindexed paths/errors: " + errors.Count)
          .AppendLine("Workspace diagnostics: " + workspaceErrors.Count)
          .AppendLine()
          .AppendLine("Classification is NOT whole-program reachability. Do not remove candidates mechanically.");
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), md.ToString());
        Console.WriteLine(md.ToString());
        foreach (var error in errors.Take(25)) Console.Error.WriteLine("COVERAGE: " + error);
        if (!complete)
        {
            Console.Error.WriteLine("AUDIT INCOMPLETE: refuse to publish tier counts as final.");
            return 3;
        }
        return 0;
    }

    private static void Collect(
        SemanticModel model, SyntaxNode root, string path, string category,
        bool collectDeclarations, string projectName,
        Dictionary<string, Record> symbols, Dictionary<string, Uses> references)
    {
        foreach (var node in root.DescendantNodesAndSelf())
        {
            if (collectDeclarations && IsDeclaration(node))
            {
                var symbol = model.GetDeclaredSymbol(node);
                string? key = symbol is null ? null : Identity(symbol);
                if (key is not null && symbol is not null && !symbol.IsImplicitlyDeclared)
                {
                    // Include source location in declaration identity to preserve partial definitions.
                    string declaration = key + "|" + path + ":" + node.SpanStart;
                    if (!symbols.ContainsKey(declaration))
                        symbols.Add(declaration, new Record
                        {
                            Key = key,
                            Name = symbol.Name,
                            StaticField = symbol is IFieldSymbol { IsStatic: true },
                            EnumConstant = symbol is IFieldSymbol fieldSymbol &&
                                           fieldSymbol.ContainingType?.TypeKind == TypeKind.Enum,
                            Path = path,
                            Line = model.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Line + 1,
                            Assembly = symbol.ContainingAssembly?.Name ?? projectName,
                            Kind = symbol.Kind.ToString(),
                            Access = symbol.DeclaredAccessibility.ToString(),
                            Signature = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                            RootRisk = RootRisk(symbol)
                        });
                }
            }
            if (node is not SimpleNameSyntax and not ObjectCreationExpressionSyntax
                    and not ImplicitObjectCreationExpressionSyntax and not AttributeSyntax)
                continue;
            var info = model.GetSymbolInfo(node);
            var target = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            string? referenceId = target is null ? null : Identity(target);
            if (referenceId is null) continue;
            if (!references.TryGetValue(referenceId, out var uses))
                references.Add(referenceId, uses = new Uses());
            uses.Add(category, path, model.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Line + 1);
        }
    }

    private static bool IsDeclaration(SyntaxNode node)
        => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax
            or EnumMemberDeclarationSyntax or BaseMethodDeclarationSyntax
            or PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax
            || node is VariableDeclaratorSyntax v &&
               v.Parent is VariableDeclarationSyntax { Parent: BaseFieldDeclarationSyntax };

    private static string? Identity(ISymbol symbol)
    {
        if (symbol is IAliasSymbol alias) symbol = alias.Target;
        if (symbol is IMethodSymbol { ReducedFrom: not null } method)
            symbol = method.ReducedFrom;
        symbol = symbol.OriginalDefinition;
        if (symbol.ContainingAssembly is null) return null;
        return symbol.ContainingAssembly.Name + "|" +
               (symbol.GetDocumentationCommentId() ??
                symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
    }

    private static string RootRisk(ISymbol symbol)
    {
        var reasons = new List<string>();
        if (symbol is IMethodSymbol method)
        {
            if (method.IsOverride || method.ExplicitInterfaceImplementations.Length > 0)
                reasons.Add("virtual-or-interface-dispatch");
            if (method.IsExtensionMethod)
                reasons.Add("extension-dispatch/compilation-review");
        }
        if (symbol is IPropertySymbol property &&
            (property.IsOverride || property.ExplicitInterfaceImplementations.Length > 0))
            reasons.Add("property-interface-dispatch");
        if (symbol is IEventSymbol eventSymbol &&
            (eventSymbol.IsOverride || eventSymbol.ExplicitInterfaceImplementations.Length > 0))
            reasons.Add("event-interface-dispatch");
        if (symbol.GetAttributes().Length > 0)
            reasons.Add("attribute-discovery");
        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor })
            reasons.Add("constructor-activation");
        if (symbol.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected
                or Accessibility.ProtectedAndInternal or Accessibility.ProtectedOrInternal)
            reasons.Add("public-external-API");
        return string.Join("; ", reasons);
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, Path.GetFullPath(path, root)).Replace('\\', '/');

    private static bool IsBuildOutput(string path) =>
        path.Split('/').Any(part => part is "obj" or "bin" or ".git");

    private static string ClassifyPath(string path) =>
        path.StartsWith("src/", StringComparison.Ordinal) ? "production" :
        path.StartsWith("test/SharpLink.Benchmarks/", StringComparison.Ordinal) ? "benchmarks" :
        path.StartsWith("test/", StringComparison.Ordinal) ? "tests" : "examples";

    private static void WriteCsv(string path, IEnumerable<Record> records)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.WriteLine("path,line,assembly,kind,visibility,name,static_field,enum_constant,signature,production,generated,tests,benchmarks,examples,root,samples");
        foreach (var record in records)
        {
            writer.WriteLine(string.Join(",", new[]
            {
                record.Path, record.Line.ToString(), record.Assembly,
                record.Kind, record.Access, record.Name, record.StaticField.ToString(),
                record.EnumConstant.ToString(), record.Signature,
                record.Production.ToString(), record.Generated.ToString(), record.Tests.ToString(),
                record.Benchmarks.ToString(), record.Examples.ToString(),
                record.RootRisk, record.ReferenceSamples
            }.Select(Escape)));
        }
    }
    private static string Escape(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
}
