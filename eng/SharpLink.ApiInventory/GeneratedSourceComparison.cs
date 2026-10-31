using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal sealed record LoggerGeneratorBinding(string AssemblyIdentity, string AssemblySha256, string ModuleVersionId, string GeneratorType)
{
    internal const string AssemblySimpleName = "Microsoft.Extensions.Logging.Generators";
    internal const string TypeName = "Microsoft.Extensions.Logging.Generators.LoggerMessageGenerator";
    internal const string Hint = "LoggerMessage.g.cs";
    internal const string LogicalPath = "generated/" + AssemblySimpleName + "/" + TypeName + "/" + Hint;

    internal static LoggerGeneratorBinding FromLoaded(string path, Type generatorType)
    {
        var identity = AssemblyName.GetAssemblyName(path);
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        var hash = InventoryReport.FileHash(path);
        if (identity.Name != AssemblySimpleName || generatorType.FullName != TypeName ||
            generatorType.Assembly.FullName != identity.FullName || generatorType.Module.ModuleVersionId != mvid ||
            string.IsNullOrEmpty(generatorType.Assembly.Location) || InventoryReport.FileHash(generatorType.Assembly.Location) != hash ||
            (!typeof(IIncrementalGenerator).IsAssignableFrom(generatorType) && !typeof(ISourceGenerator).IsAssignableFrom(generatorType)))
            throw new InvalidOperationException("Logger ordering comparison cannot bind the actual loaded generator to its analyzer bytes.");
        return new LoggerGeneratorBinding(identity.FullName, hash, mvid.ToString(), generatorType.FullName);
    }

    internal static LoggerGeneratorBinding FromFile(string path)
    {
        var loaded = Assembly.LoadFrom(Path.GetFullPath(path));
        return FromLoaded(path, loaded.GetType(TypeName, throwOnError: true)!);
    }
}

internal sealed record GeneratedComparison(
    string Path, bool Equivalent, string MatchKind, string CompilerSha256, string WorkspaceSha256,
    string CompilerSnapshotPath, string WorkspaceSnapshotPath, LoggerGeneratorBinding? GeneratorBinding,
    string[] CompilerTypeOrder, string[] WorkspaceTypeOrder, string? Rejection);

internal static class GeneratedSourceComparison
{
    internal static GeneratedComparison Compare(string path, string compiler, string workspace,
        CSharpParseOptions options, LoggerGeneratorBinding? binding)
    {
        var compilerHash = InventoryReport.Hash(compiler);
        var workspaceHash = InventoryReport.Hash(workspace);
        var result = new GeneratedComparison(path, false, "different", compilerHash, workspaceHash,
            "build-" + path, "workspace-" + path, binding, [], [], null);
        if (compilerHash == workspaceHash)
            return result with { Equivalent = true, MatchKind = "exact" };
        if (path != LoggerGeneratorBinding.LogicalPath || binding is null)
            return result with { Rejection = "Text hashes differ and there is no bound LoggerMessage ordering exception." };
        try
        {
            var expected = Partition(compiler, options, binding);
            var actual = Partition(workspace, options, binding);
            result = result with { CompilerTypeOrder = expected.Order, WorkspaceTypeOrder = actual.Order };
            if (expected.Prefix != actual.Prefix || expected.Suffix != actual.Suffix || !expected.Separators.SequenceEqual(actual.Separators))
                return result with { Rejection = "Text outside namespace blocks differs." };
            if (expected.Blocks.Count != actual.Blocks.Count || expected.Blocks.Any(pair =>
                !actual.Blocks.TryGetValue(pair.Key, out var block) || block != pair.Value))
                return result with { Rejection = "Namespace type identity or complete block content differs." };
            return result with { Equivalent = true, MatchKind = "logger-independent-namespace-order" };
        }
        catch (InvalidOperationException exception)
        {
            return result with { Rejection = exception.Message };
        }
    }

    private sealed record Parts(string Prefix, string Suffix, string[] Separators, string[] Order, Dictionary<string, string> Blocks);

    private static Parts Partition(string text, CSharpParseOptions options, LoggerGeneratorBinding binding)
    {
        var tree = CSharpSyntaxTree.ParseText(text, options);
        if (tree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            throw new InvalidOperationException("Generated source has parse errors.");
        var root = tree.GetCompilationUnitRoot();
        if (root.Usings.Count != 0 || root.Externs.Count != 0 || root.AttributeLists.Count != 0 ||
            root.Members.Count < 2 || root.Members.Any(member => member is not NamespaceDeclarationSyntax))
            throw new InvalidOperationException("Only multiple independent block namespaces are eligible (no using/extern/global attributes/statements).");
        var namespaces = root.Members.Cast<NamespaceDeclarationSyntax>().ToArray();
        var directives = root.DescendantTrivia(descendIntoTrivia: true).Where(trivia => trivia.IsDirective).ToArray();
        if (directives.Length != 1 || directives[0].GetStructure() is not NullableDirectiveTriviaSyntax nullable ||
            nullable.ToString().Trim() != "#nullable enable" || directives[0].FullSpan.End > namespaces[0].SpanStart)
            throw new InvalidOperationException("Only one fixed leading #nullable enable is eligible; every other directive is rejected.");
        foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
        {
            var name = attribute.Name.ToString();
            var arguments = attribute.ArgumentList?.Arguments ?? default;
            var generatedCode = name == "global::System.CodeDom.Compiler.GeneratedCodeAttribute" && arguments.Count == 2 &&
                arguments.All(argument => argument.NameColon is null && argument.NameEquals is null && argument.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)) &&
                ((LiteralExpressionSyntax)arguments[0].Expression).Token.ValueText == LoggerGeneratorBinding.AssemblySimpleName &&
                ((LiteralExpressionSyntax)arguments[1].Expression).Token.ValueText == new AssemblyName(binding.AssemblyIdentity).Version!.ToString();
            var editorBrowsable = name == "global::System.ComponentModel.EditorBrowsableAttribute" && arguments.Count == 1 &&
                arguments[0].NameColon is null && arguments[0].NameEquals is null &&
                arguments[0].Expression.ToString() == "global::System.ComponentModel.EditorBrowsableState.Never";
            if (!generatedCode && !editorBrowsable)
                throw new InvalidOperationException("Unsupported attribute; ModuleInitializer, aliases and other attributes are not eligible.");
        }
        var blocks = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var ns in namespaces)
        {
            if (ns.Usings.Count != 0 || ns.Externs.Count != 0 || ns.Members.Count != 1 ||
                ns.Members[0] is not ClassDeclarationSyntax type || !type.Modifiers.Any(SyntaxKind.PartialKeyword))
                throw new InvalidOperationException("Each namespace must contain exactly one partial class and no using/extern.");
            var arity = type.TypeParameterList?.Parameters.Count ?? 0;
            var key = NamespaceIdentity(ns.Name) + "." + type.Identifier.ValueText + "`" + arity;
            if (!blocks.TryAdd(key, text.Substring(ns.SpanStart, ns.Span.Length)))
                throw new InvalidOperationException("Duplicate type identity; same-type partial reordering is not eligible.");
            order.Add(key);
        }
        var separators = namespaces.Zip(namespaces.Skip(1), (left, right) => text[left.Span.End..right.SpanStart]).ToArray();
        var suffix = text[namespaces[^1].Span.End..];
        if (separators.Any(separator => !string.IsNullOrWhiteSpace(separator)) || !string.IsNullOrWhiteSpace(suffix))
            throw new InvalidOperationException("Text between/after namespaces must be whitespace only.");
        return new Parts(text[..namespaces[0].SpanStart], suffix, separators, order.ToArray(), blocks);
    }

    private static string NamespaceIdentity(NameSyntax name) => name switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        QualifiedNameSyntax qualified => NamespaceIdentity(qualified.Left) + "." + NamespaceIdentity(qualified.Right),
        _ => throw new InvalidOperationException("Unsupported namespace name syntax."),
    };
}
