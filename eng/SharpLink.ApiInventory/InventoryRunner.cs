using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;

internal static class InventoryRunner
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var root = Path.GetFullPath(args[0]);
        var projectPath = Path.GetFullPath(args[1]);
        var tfm = args[2];
        var output = Path.GetFullPath(args[5]);
        Directory.CreateDirectory(output);
        var failures = new ConcurrentQueue<string>();
        var diagnostics = new List<object>();
        var documents = new List<DocumentEvidence>();
        var analyzers = new List<object>();
        var inputs = new Dictionary<string, object?>();
        var generatedComparisons = new List<GeneratedComparison>();
        SymbolInventory? inventory = null;
        try
        {
            using var workspace = MSBuildWorkspace.Create(new Dictionary<string, string>
            {
                ["Configuration"] = args[3],
                ["AlwaysCompileMarkupFilesInSeparateDomain"] = "false",
            });
            // The wrapper builds actual project references with MSBuild's normal TFM
            // negotiation first. Use those outputs, not globally retargeted source refs.
            workspace.LoadMetadataForReferencedProjects = true;
            workspace.SkipUnrecognizedProjects = false;
            using var handler = workspace.RegisterWorkspaceFailedHandler(failure => failures.Enqueue($"workspace {failure.Diagnostic.Kind}: {failure.Diagnostic.Message}"));
            await workspace.OpenProjectAsync(projectPath);
            // Workspace loads every inner target of a multitargeted project. Select
            // the MSBuild-evaluated output path, not OpenProjectAsync's first target.
            // A global TargetFramework here would incorrectly retarget references.
            var candidates = workspace.CurrentSolution.Projects.Where(candidate =>
                candidate.FilePath is not null && Path.GetFullPath(candidate.FilePath) == projectPath &&
                candidate.OutputFilePath is not null && Path.GetFullPath(candidate.OutputFilePath) == Path.GetFullPath(args[7])).ToArray();
            if (candidates.Length != 1)
                throw new InvalidOperationException($"Expected exactly one workspace target for {projectPath} -> {args[7]}, found {candidates.Length}.");
            var project = candidates[0];
            LoggerGeneratorBinding? loggerBinding = null;
            foreach (var reference in project.AnalyzerReferences.OfType<AnalyzerFileReference>())
            {
                reference.AnalyzerLoadFailed += (_, failure) => failures.Enqueue($"analyzer {failure.ErrorCode}: {failure.Message}");
                var generators = reference.GetGenerators(LanguageNames.CSharp);
                foreach (var generator in generators.Where(generator => generator.GetGeneratorType().FullName == LoggerGeneratorBinding.TypeName))
                {
                    if (loggerBinding is not null)
                        throw new InvalidOperationException("Multiple LoggerMessage generators cannot be unambiguously bound.");
                    loggerBinding = LoggerGeneratorBinding.FromLoaded(reference.FullPath, generator.GetGeneratorType());
                }
                analyzers.Add(new
                {
                    Path = InventoryReport.Relative(root, reference.FullPath),
                    Sha256 = InventoryReport.FileHash(reference.FullPath),
                    Identity = AssemblyName.GetAssemblyName(reference.FullPath).FullName,
                    ModuleVersionId = ReadMvid(reference.FullPath),
                    GeneratorTypes = generators.Select(generator => generator.GetGeneratorType().FullName).Order(StringComparer.Ordinal).ToArray(),
                    GeneratorCount = generators.Length,
                });
            }
            var compilation = await project.GetCompilationAsync() ?? throw new InvalidOperationException("Project returned no compilation.");
            var generated = (await project.GetSourceGeneratedDocumentsAsync()).ToArray();
            var treeSources = new Dictionary<SyntaxTree, DocumentEvidence>();
            foreach (var document in project.Documents.Concat<Microsoft.CodeAnalysis.Document>(generated))
            {
                var tree = await document.GetSyntaxTreeAsync() ?? throw new InvalidOperationException($"No syntax tree: {document.Name}");
                var text = await document.GetTextAsync();
                var generatedDocument = document as SourceGeneratedDocument;
                var path = generatedDocument is null
                    ? InventoryReport.Relative(root, document.FilePath ?? throw new InvalidOperationException("Source document has no path."))
                    : "generated/" + GeneratedKey(tree.FilePath, generatedDocument.HintName, args[6]);
                var evidence = new DocumentEvidence(path, generatedDocument is null ? "source" : "source-generated",
                    InventoryReport.Hash(text.ToString()), generatedDocument?.HintName,
                    generatedDocument is null ? null : path.Split('/')[1], generatedDocument is null ? null : path.Split('/')[2],
                    generatedDocument is null ? null : "workspace-" + path);
                if (evidence.SnapshotPath is not null)
                {
                    var snapshot = Path.Combine(output, evidence.SnapshotPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
                    File.WriteAllText(snapshot, text.ToString());
                }
                treeSources.Add(tree, evidence);
                documents.Add(evidence);
            }
            if (documents.GroupBy(document => document.Path, StringComparer.Ordinal).Any(group => group.Count() > 1))
                failures.Enqueue("Duplicate source/generated document paths.");
            if (!compilation.SyntaxTrees.ToHashSet().SetEquals(treeSources.Keys))
                failures.Enqueue("Compilation syntax trees differ from ordinary + generated document inventory.");
            inputs["documentOrder"] = documents.Select(document => document.Path).ToArray();
            inputs["syntaxTreeOrder"] = compilation.SyntaxTrees.Select(tree => treeSources[tree].Path).ToArray();

            // Workspace's public Project API does not expose GeneratorDriver diagnostics.
            // Require a successful real compiler rebuild (wrapper), and compare every
            // generated file against its output, so a workspace generator cannot silently
            // disappear or produce different declarations. The sole bounded ordering
            // exception records both raw hashes and requires unchanged complete blocks.
            var buildGenerated = Directory.GetFiles(args[6], "*.cs", SearchOption.AllDirectories)
                .ToDictionary(path => "generated/" + InventoryReport.Relative(args[6], path),
                    File.ReadAllText, StringComparer.Ordinal);
            var workspaceGenerated = documents.Where(document => document.Kind == "source-generated")
                .ToDictionary(document => document.Path, StringComparer.Ordinal);
            if (buildGenerated.Count != workspaceGenerated.Count ||
                buildGenerated.Keys.Any(path => !workspaceGenerated.ContainsKey(path)))
                failures.Enqueue("Workspace generated documents differ from the real compiler output (names/count).");
            foreach (var pair in buildGenerated.Where(pair => workspaceGenerated.ContainsKey(pair.Key)))
            {
                var document = workspaceGenerated[pair.Key];
                var binding = document.GeneratorAssembly == LoggerGeneratorBinding.AssemblySimpleName &&
                    document.GeneratorType == LoggerGeneratorBinding.TypeName && document.HintName == LoggerGeneratorBinding.Hint
                    ? loggerBinding : null;
                var comparison = GeneratedSourceComparison.Compare(pair.Key, pair.Value,
                    File.ReadAllText(Path.Combine(output, document.SnapshotPath!)), (CSharpParseOptions)project.ParseOptions!, binding);
                generatedComparisons.Add(comparison);
                if (!comparison.Equivalent)
                    failures.Enqueue($"Generated document {pair.Key}: {comparison.Rejection}");
            }
            foreach (var diagnostic in compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity != DiagnosticSeverity.Hidden))
            {
                var location = diagnostic.Location;
                diagnostics.Add(new
                {
                    diagnostic.Id,
                    Severity = diagnostic.Severity.ToString(),
                    Message = diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture),
                    Path = location.IsInSource ? treeSources[location.SourceTree!].Path : null,
                    Line = location.IsInSource ? location.GetLineSpan().StartLinePosition.Line + 1 : 0
                });
                if (diagnostic.Severity == DiagnosticSeverity.Error || diagnostic.Id is "CS8784" or "CS8785" or "CS8032" or "CS8033" or "CS8034")
                    failures.Enqueue($"compilation {diagnostic.Id}: {diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            var targetFramework = compilation.Assembly.GetAttributes().FirstOrDefault(attribute =>
                attribute.AttributeClass?.ToDisplayString() == "System.Runtime.Versioning.TargetFrameworkAttribute")?.ConstructorArguments.FirstOrDefault().Value?.ToString();
            inputs["assemblyName"] = compilation.AssemblyName;
            inputs["outputFilePath"] = InventoryReport.Relative(root, project.OutputFilePath!);
            inputs["targetFrameworkMoniker"] = targetFramework;
            inputs["languageVersion"] = ((CSharpParseOptions)project.ParseOptions!).LanguageVersion.ToString();
            inputs["preprocessorSymbols"] = ((CSharpParseOptions)project.ParseOptions!).PreprocessorSymbolNames.Order(StringComparer.Ordinal).ToArray();
            inputs["nullableContextOptions"] = compilation.Options.NullableContextOptions.ToString();
            inputs["projectReferencesLoadedAsSource"] = project.ProjectReferences.Select(reference =>
                InventoryReport.Relative(root, project.Solution.GetProject(reference.ProjectId)!.FilePath!)).Order(StringComparer.Ordinal).ToArray();
            if (project.ProjectReferences.Any())
                failures.Enqueue("Some referenced projects were loaded as source instead of their built metadata; TFM isolation is not verified.");
            inputs["metadataReferences"] = project.MetadataReferences.OfType<PortableExecutableReference>()
                .Select(reference => reference.FilePath!).Order(StringComparer.Ordinal)
                .Select(path => new { Path = InventoryReport.Relative(root, path), Sha256 = InventoryReport.FileHash(path) }).ToArray();
            inputs["additionalFiles"] = await DocumentInputsAsync(root, project.AdditionalDocuments);
            inputs["analyzerConfigs"] = await DocumentInputsAsync(root, project.AnalyzerConfigDocuments);
            inventory = new SymbolInventory(InventoryReport.Relative(root, projectPath), tfm, compilation.AssemblyName!, treeSources);
            inventory.Collect(compilation.Assembly);
        }
        catch (Exception exception)
        {
            failures.Enqueue(exception.ToString());
        }
        var errors = failures.Order(StringComparer.Ordinal).ToArray();
        InventoryReport.WriteLines(Path.Combine(output, "symbols.jsonl"), (inventory?.Symbols ?? []).OrderBy(symbol => symbol.Id, StringComparer.Ordinal));
        InventoryReport.WriteLines(Path.Combine(output, "relations.jsonl"), (inventory?.Relations ?? []).OrderBy(relation => relation.Source, StringComparer.Ordinal)
            .ThenBy(relation => relation.Kind, StringComparer.Ordinal).ThenBy(relation => relation.Target, StringComparer.Ordinal));
        InventoryReport.Write(Path.Combine(output, "target.json"), new
        {
            SchemaVersion = 1,
            Project = InventoryReport.Relative(root, projectPath),
            Framework = tfm,
            Configuration = args[3],
            Complete = errors.Length == 0,
            RoslynVersion = typeof(Compilation).Assembly.GetName().Version!.ToString(),
            Failures = errors,
            Diagnostics = diagnostics,
            Inputs = inputs,
            Documents = documents.OrderBy(document => document.Path, StringComparer.Ordinal).ToArray(),
            GeneratedComparisons = generatedComparisons.OrderBy(comparison => comparison.Path, StringComparer.Ordinal).ToArray(),
            Analyzers = analyzers.OrderBy(value => System.Text.Json.JsonSerializer.Serialize(value), StringComparer.Ordinal).ToArray(),
            SymbolCount = inventory?.Symbols.Count ?? 0,
        });
        Console.WriteLine($"{InventoryReport.Relative(root, projectPath)} [{tfm}]: {inventory?.Symbols.Count ?? 0} symbols; {errors.Length} failure(s).");
        return errors.Length == 0 ? 0 : 1;
    }

    private static string GeneratedKey(string path, string hint, string buildDirectory)
    {
        var normalized = path.Replace('\\', '/');
        // Roslyn's generated path may be relative or rooted under the source project.
        // Match the full generator assembly/type/hint path, never just a hint basename.
        var matches = Directory.GetFiles(buildDirectory, "*.cs", SearchOption.AllDirectories)
            .Select(file => InventoryReport.Relative(buildDirectory, file))
            .Where(relative => normalized == relative || normalized.EndsWith("/" + relative, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"Cannot uniquely bind generated document {path} ({hint}) to compiler output: {matches.Length} matches.");
        return matches[0];
    }

    private static async Task<object[]> DocumentInputsAsync(string root, IEnumerable<TextDocument> documents)
    {
        var result = new List<object>();
        foreach (var document in documents.OrderBy(document => document.FilePath, StringComparer.Ordinal))
            result.Add(new { Path = InventoryReport.Relative(root, document.FilePath!), Sha256 = InventoryReport.Hash((await document.GetTextAsync()).ToString()) });
        return result.ToArray();
    }

    private static string ReadMvid(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        return reader.GetGuid(reader.GetModuleDefinition().Mvid).ToString();
    }
}
