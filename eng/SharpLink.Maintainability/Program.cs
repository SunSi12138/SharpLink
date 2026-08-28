using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

const int LargeMethodLocThreshold = 80;
const int ComplexMethodThreshold = 15;
const int TopCount = 25;

var options = ParseOptions(args);
var repoRoot = Path.GetFullPath(options.Root);
var outputDirectory = Path.GetFullPath(options.OutputDirectory, repoRoot);

if (!Directory.Exists(Path.Combine(repoRoot, "src")) || !Directory.Exists(Path.Combine(repoRoot, "test")))
{
    Console.Error.WriteLine($"Repository root must contain src/ and test/: {repoRoot}");
    return 2;
}

var sourceRef = options.SourceRef ?? TryGetGitHead(repoRoot) ?? "working-tree";
var files = new List<FileMetric>();
var methods = new List<MethodMetric>();

AnalyzeDomain(repoRoot, "source", "src", files, methods);
AnalyzeDomain(repoRoot, "test", "test", files, methods);

var orderedFiles = files
    .OrderBy(static file => file.Domain, StringComparer.Ordinal)
    .ThenBy(static file => file.Path, StringComparer.Ordinal)
    .ToArray();
var orderedMethods = methods
    .OrderBy(static method => method.Domain, StringComparer.Ordinal)
    .ThenBy(static method => method.Path, StringComparer.Ordinal)
    .ThenBy(static method => method.StartLine)
    .ThenBy(static method => method.Name, StringComparer.Ordinal)
    .ToArray();

var largeMethods = orderedMethods
    .Where(static method => method.Loc >= LargeMethodLocThreshold)
    .OrderByDescending(static method => method.Loc)
    .ThenByDescending(static method => method.CyclomaticComplexity)
    .ThenBy(static method => method.Path, StringComparer.Ordinal)
    .ThenBy(static method => method.StartLine)
    .ThenBy(static method => method.Name, StringComparer.Ordinal)
    .ToArray();

var complexMethods = orderedMethods
    .Where(static method => method.CyclomaticComplexity >= ComplexMethodThreshold)
    .OrderByDescending(static method => method.CyclomaticComplexity)
    .ThenByDescending(static method => method.Loc)
    .ThenBy(static method => method.Path, StringComparer.Ordinal)
    .ThenBy(static method => method.StartLine)
    .ThenBy(static method => method.Name, StringComparer.Ordinal)
    .ToArray();

var report = new Report(
    SchemaVersion: 1,
    SourceRef: sourceRef,
    Definitions: new Definitions(
        Loc: "Physical line count from Roslyn SourceText; generated build output under bin/ and obj/ is excluded.",
        MethodLoc: "Inclusive physical line span for C# method-like declarations.",
        CyclomaticComplexity: "1 plus if/loop/catch/case/switch-expression-arm/conditional-expression/&&/|| decision points inside the method body; nested local functions, lambdas, and anonymous methods are excluded.",
        UsingDependencyCount: "Distinct namespace targets from non-global using directives in the file; this is a lightweight coupling proxy.",
        LargeMethodLocThreshold: LargeMethodLocThreshold,
        ComplexMethodThreshold: ComplexMethodThreshold),
    Summary: new Dictionary<string, DomainSummary>(StringComparer.Ordinal)
    {
        ["source"] = BuildSummary("source", orderedFiles, orderedMethods, largeMethods, complexMethods),
        ["test"] = BuildSummary("test", orderedFiles, orderedMethods, largeMethods, complexMethods),
    },
    Files: orderedFiles,
    LargeMethods: largeMethods,
    ComplexMethods: complexMethods);

Directory.CreateDirectory(outputDirectory);
var jsonPath = Path.Combine(outputDirectory, "report.json");
var markdownPath = Path.Combine(outputDirectory, "report.md");

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};
File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, jsonOptions) + "\n", new UTF8Encoding(false));
File.WriteAllText(markdownPath, BuildMarkdown(report).ReplaceLineEndings("\n"), new UTF8Encoding(false));

Console.WriteLine(Path.GetRelativePath(repoRoot, jsonPath).Replace('\\', '/'));
Console.WriteLine(Path.GetRelativePath(repoRoot, markdownPath).Replace('\\', '/'));
return 0;

static Options ParseOptions(string[] args)
{
    var root = Directory.GetCurrentDirectory();
    var output = Path.Combine("artifacts", "maintainability");
    string? sourceRef = null;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--root" when i + 1 < args.Length:
                root = args[++i];
                break;
            case "--output" when i + 1 < args.Length:
                output = args[++i];
                break;
            case "--source-ref" when i + 1 < args.Length:
                sourceRef = args[++i];
                break;
            default:
                throw new ArgumentException($"Unknown or incomplete argument: {args[i]}");
        }
    }

    return new Options(root, output, sourceRef);
}

static void AnalyzeDomain(
    string repoRoot,
    string domain,
    string directoryName,
    List<FileMetric> files,
    List<MethodMetric> methods)
{
    var domainRoot = Path.Combine(repoRoot, directoryName);
    var enumerationOptions = new EnumerationOptions
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };
    var paths = Directory.EnumerateFiles(domainRoot, "*.cs", enumerationOptions)
        .Select(path => new
        {
            FullPath = path,
            RelativePath = Path.GetRelativePath(repoRoot, path).Replace('\\', '/'),
        })
        .Where(static file => !ContainsIgnoredSegment(file.RelativePath))
        .OrderBy(static file => file.RelativePath, StringComparer.Ordinal);

    foreach (var file in paths)
    {
        var fullPath = file.FullPath;
        var relativePath = file.RelativePath;
        var sourceText = SourceText.From(File.ReadAllText(fullPath, Encoding.UTF8), Encoding.UTF8);
        var tree = CSharpSyntaxTree.ParseText(sourceText, new CSharpParseOptions(LanguageVersion.Latest), path: relativePath);
        var root = tree.GetRoot();
        var fileMethods = GetMethodMetrics(domain, relativePath, tree, root).ToArray();
        methods.AddRange(fileMethods);

        var usingDependencies = root.DescendantNodes(descendIntoTrivia: false)
            .OfType<UsingDirectiveSyntax>()
            .Where(static directive => directive.GlobalKeyword.IsKind(SyntaxKind.None))
            .Select(static directive => directive.Name?.ToString())
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Count();

        files.Add(new FileMetric(
            Domain: domain,
            Path: relativePath,
            Loc: sourceText.Lines.Count,
            MethodCount: fileMethods.Length,
            MaxMethodLoc: fileMethods.Length == 0 ? 0 : fileMethods.Max(static method => method.Loc),
            MaxCyclomaticComplexity: fileMethods.Length == 0 ? 0 : fileMethods.Max(static method => method.CyclomaticComplexity),
            UsingDependencyCount: usingDependencies));
    }
}

static bool ContainsIgnoredSegment(string relativePath)
{
    var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    return segments.Any(static segment =>
        string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
        || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
}

static IEnumerable<MethodMetric> GetMethodMetrics(string domain, string path, SyntaxTree tree, SyntaxNode root)
{
    foreach (var node in root.DescendantNodes(descendIntoTrivia: false))
    {
        if (!TryDescribeMethod(node, out var name, out var bodyNode))
        {
            continue;
        }

        var span = tree.GetLineSpan(node.Span);
        var startLine = span.StartLinePosition.Line + 1;
        var endLine = span.EndLinePosition.Line + 1;
        var complexity = ComputeCyclomaticComplexity(bodyNode);

        yield return new MethodMetric(
            Domain: domain,
            Path: path,
            Name: name,
            StartLine: startLine,
            Loc: endLine - startLine + 1,
            CyclomaticComplexity: complexity);
    }
}

static bool TryDescribeMethod(SyntaxNode node, out string name, out SyntaxNode bodyNode)
{
    switch (node)
    {
        case MethodDeclarationSyntax method:
            name = method.Identifier.ValueText;
            bodyNode = (SyntaxNode?)method.Body ?? (SyntaxNode?)method.ExpressionBody ?? method;
            return true;
        case ConstructorDeclarationSyntax constructor:
            name = constructor.Identifier.ValueText;
            bodyNode = (SyntaxNode?)constructor.Body ?? (SyntaxNode?)constructor.ExpressionBody ?? constructor;
            return true;
        case DestructorDeclarationSyntax destructor:
            name = "~" + destructor.Identifier.ValueText;
            bodyNode = (SyntaxNode?)destructor.Body ?? (SyntaxNode?)destructor.ExpressionBody ?? destructor;
            return true;
        case OperatorDeclarationSyntax operatorDeclaration:
            name = "operator " + operatorDeclaration.OperatorToken.ValueText;
            bodyNode = (SyntaxNode?)operatorDeclaration.Body ?? (SyntaxNode?)operatorDeclaration.ExpressionBody ?? operatorDeclaration;
            return true;
        case ConversionOperatorDeclarationSyntax conversion:
            name = "operator " + conversion.Type;
            bodyNode = (SyntaxNode?)conversion.Body ?? (SyntaxNode?)conversion.ExpressionBody ?? conversion;
            return true;
        case LocalFunctionStatementSyntax localFunction:
            name = localFunction.Identifier.ValueText;
            bodyNode = (SyntaxNode?)localFunction.Body ?? (SyntaxNode?)localFunction.ExpressionBody ?? localFunction;
            return true;
        default:
            name = string.Empty;
            bodyNode = node;
            return false;
    }
}

static int ComputeCyclomaticComplexity(SyntaxNode bodyNode)
{
    var complexity = 1;
    foreach (var node in bodyNode.DescendantNodesAndSelf(
        current => current == bodyNode || !IsNestedExecutableBody(current),
        descendIntoTrivia: false))
    {
        complexity += node switch
        {
            IfStatementSyntax => 1,
            ForStatementSyntax => 1,
            ForEachStatementSyntax => 1,
            ForEachVariableStatementSyntax => 1,
            WhileStatementSyntax => 1,
            DoStatementSyntax => 1,
            CatchClauseSyntax => 1,
            CaseSwitchLabelSyntax => 1,
            CasePatternSwitchLabelSyntax => 1,
            SwitchExpressionArmSyntax => 1,
            ConditionalExpressionSyntax => 1,
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression) => 1,
            _ => 0,
        };
    }

    return complexity;
}

static bool IsNestedExecutableBody(SyntaxNode node) =>
    node is MethodDeclarationSyntax
        or ConstructorDeclarationSyntax
        or DestructorDeclarationSyntax
        or OperatorDeclarationSyntax
        or ConversionOperatorDeclarationSyntax
        or LocalFunctionStatementSyntax
        or SimpleLambdaExpressionSyntax
        or ParenthesizedLambdaExpressionSyntax
        or AnonymousMethodExpressionSyntax;

static DomainSummary BuildSummary(
    string domain,
    IReadOnlyCollection<FileMetric> files,
    IReadOnlyCollection<MethodMetric> methods,
    IReadOnlyCollection<MethodMetric> largeMethods,
    IReadOnlyCollection<MethodMetric> complexMethods)
{
    var domainFiles = files.Where(file => string.Equals(file.Domain, domain, StringComparison.Ordinal)).ToArray();
    var domainMethods = methods.Where(method => string.Equals(method.Domain, domain, StringComparison.Ordinal)).ToArray();
    return new DomainSummary(
        Files: domainFiles.Length,
        Loc: domainFiles.Sum(static file => file.Loc),
        Methods: domainMethods.Length,
        LargeMethods: largeMethods.Count(method => string.Equals(method.Domain, domain, StringComparison.Ordinal)),
        ComplexMethods: complexMethods.Count(method => string.Equals(method.Domain, domain, StringComparison.Ordinal)));
}

static string BuildMarkdown(Report report)
{
    var builder = new StringBuilder();
    builder.AppendLine("# SharpLink maintainability report");
    builder.AppendLine();
    builder.Append("Source ref: `").Append(report.SourceRef).AppendLine("`");
    builder.AppendLine();
    builder.AppendLine("## Summary");
    builder.AppendLine();
    builder.AppendLine("| Domain | Files | LOC | Methods | Large methods | Complex methods |");
    builder.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: |");
    foreach (var domain in new[] { "source", "test" })
    {
        var summary = report.Summary[domain];
        builder.Append("| ").Append(domain)
            .Append(" | ").Append(summary.Files.ToString(CultureInfo.InvariantCulture))
            .Append(" | ").Append(summary.Loc.ToString(CultureInfo.InvariantCulture))
            .Append(" | ").Append(summary.Methods.ToString(CultureInfo.InvariantCulture))
            .Append(" | ").Append(summary.LargeMethods.ToString(CultureInfo.InvariantCulture))
            .Append(" | ").Append(summary.ComplexMethods.ToString(CultureInfo.InvariantCulture))
            .AppendLine(" |");
    }

    AppendFileTable(builder, "Top source files by LOC", report.Files.Where(static file => file.Domain == "source"));
    AppendFileTable(builder, "Top test files by LOC", report.Files.Where(static file => file.Domain == "test"));
    AppendMethodTable(builder, $"Top {TopCount} large methods (>= {LargeMethodLocThreshold} LOC)", report.LargeMethods);
    AppendMethodTable(builder, $"Top {TopCount} complex methods (>= {ComplexMethodThreshold})", report.ComplexMethods);

    builder.AppendLine("## Metric definitions");
    builder.AppendLine();
    builder.Append("- LOC: ").AppendLine(report.Definitions.Loc);
    builder.Append("- Method LOC: ").AppendLine(report.Definitions.MethodLoc);
    builder.Append("- Cyclomatic complexity: ").AppendLine(report.Definitions.CyclomaticComplexity);
    builder.Append("- Using dependency count: ").AppendLine(report.Definitions.UsingDependencyCount);
    return builder.ToString();
}

static void AppendFileTable(StringBuilder builder, string title, IEnumerable<FileMetric> files)
{
    builder.AppendLine();
    builder.Append("## ").AppendLine(title);
    builder.AppendLine();
    builder.AppendLine("| Path | LOC | Methods | Max method LOC | Max complexity | Using dependencies |");
    builder.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: |");
    foreach (var file in files
        .OrderByDescending(static file => file.Loc)
        .ThenByDescending(static file => file.MaxMethodLoc)
        .ThenBy(static file => file.Path, StringComparer.Ordinal)
        .Take(TopCount))
    {
        builder.Append("| `").Append(file.Path).Append("` | ")
            .Append(file.Loc.ToString(CultureInfo.InvariantCulture)).Append(" | ")
            .Append(file.MethodCount.ToString(CultureInfo.InvariantCulture)).Append(" | ")
            .Append(file.MaxMethodLoc.ToString(CultureInfo.InvariantCulture)).Append(" | ")
            .Append(file.MaxCyclomaticComplexity.ToString(CultureInfo.InvariantCulture)).Append(" | ")
            .Append(file.UsingDependencyCount.ToString(CultureInfo.InvariantCulture)).AppendLine(" |");
    }
}

static void AppendMethodTable(StringBuilder builder, string title, IEnumerable<MethodMetric> methods)
{
    builder.AppendLine();
    builder.Append("## ").AppendLine(title);
    builder.AppendLine();
    builder.AppendLine("| Domain | Method | Location | LOC | Complexity |");
    builder.AppendLine("| --- | --- | --- | ---: | ---: |");
    foreach (var method in methods.Take(TopCount))
    {
        builder.Append("| ").Append(method.Domain)
            .Append(" | `").Append(method.Name.Replace("|", "\\|", StringComparison.Ordinal)).Append('`')
            .Append(" | `").Append(method.Path).Append(':').Append(method.StartLine.ToString(CultureInfo.InvariantCulture)).Append('`')
            .Append(" | ").Append(method.Loc.ToString(CultureInfo.InvariantCulture))
            .Append(" | ").Append(method.CyclomaticComplexity.ToString(CultureInfo.InvariantCulture)).AppendLine(" |");
    }
}

static string? TryGetGitHead(string repoRoot)
{
    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "rev-parse HEAD",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is null)
        {
            return null;
        }

        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return process.ExitCode == 0 && output.Length != 0 ? output : null;
    }
    catch (Exception)
    {
        return null;
    }
}

sealed record Options(string Root, string OutputDirectory, string? SourceRef);
sealed record Definitions(
    string Loc,
    string MethodLoc,
    string CyclomaticComplexity,
    string UsingDependencyCount,
    int LargeMethodLocThreshold,
    int ComplexMethodThreshold);
sealed record DomainSummary(int Files, int Loc, int Methods, int LargeMethods, int ComplexMethods);
sealed record FileMetric(
    string Domain,
    string Path,
    int Loc,
    int MethodCount,
    int MaxMethodLoc,
    int MaxCyclomaticComplexity,
    int UsingDependencyCount);
sealed record MethodMetric(
    string Domain,
    string Path,
    string Name,
    int StartLine,
    int Loc,
    int CyclomaticComplexity);
sealed record Report(
    int SchemaVersion,
    string SourceRef,
    Definitions Definitions,
    IReadOnlyDictionary<string, DomainSummary> Summary,
    IReadOnlyList<FileMetric> Files,
    IReadOnlyList<MethodMetric> LargeMethods,
    IReadOnlyList<MethodMetric> ComplexMethods);
