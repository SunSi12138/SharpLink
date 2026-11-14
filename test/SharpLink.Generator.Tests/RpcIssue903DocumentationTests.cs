using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task Issue903DocumentedSingleMetadataEntryMustCompileAgainstActualAbstractions()
    {
        var markdown = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "doc-calls-and-streaming.md"));
        var metadataSections = markdown.ReplaceLineEndings("\n").Split("\n## Metadata\n", StringSplitOptions.None);
        Ensure(metadataSections.Length == 2, "metadata documentation section must be unique");
        var metadataSection = metadataSections[1].Split("\n## ", StringSplitOptions.None)[0];
        var codeBlock = metadataSection.Split("```csharp", StringSplitOptions.None)
            .Skip(1)
            .Select(part => part.Split("```", StringSplitOptions.None)[0])
            .Single(block => block.Contains("CreateProxy<IMyService>", StringComparison.Ordinal));
        var wrapper = $$"""
using System;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;
using SharpLink.Abstractions;

public interface IMyService : IService { Task<int> GetAsync(int id, CancellationToken token); }
public sealed class Client
{
    public RpcProxy<T> CreateProxy<T>(RpcProxyOptions? options = null) where T : IService
        => throw new NotSupportedException();
}
public static class DocumentedMetadataExample
{
    public static async Task Use(Client client, int id, CancellationToken cancellationToken)
    {
        {{codeBlock}}
    }
}
""";
        var compilation = CSharpCompilation.Create(
            "SharpLinkMetadataDocumentationCompilation",
            [
                CSharpSyntaxTree.ParseText("global using System;"),
                CSharpSyntaxTree.ParseText(wrapper)
            ],
            GeneratorTestHarness.GetPlatformReferences().Add(MetadataReference.CreateFromFile(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "SharpLink.Abstractions.dll"))),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Ensure(errors.Length == 0, $"metadata docs must compile against the real metadata/options/opaque handle API: {FormatDiagnostics(errors)}");
        return Task.CompletedTask;
    }
}
