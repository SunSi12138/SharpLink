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
    public Task Issue903DocumentedPreparedMetadataEntryMustCompileAgainstActualClient()
    {
        var markdown = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "doc-calls-and-streaming.md"));
        var codeBlock = markdown.Split("```csharp", StringSplitOptions.None)
            .Skip(1)
            .Select(part => part.Split("```", StringSplitOptions.None)[0])
            .Single(block => block.Contains("CreateProxy<IMyService>", StringComparison.Ordinal) &&
                block.Contains("RpcProxyOptions", StringComparison.Ordinal));
        var wrapper = $$"""
using System;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Sdk;

public interface IMyService : IService { Task<int> GetAsync(int id, CancellationToken token); }
public static class DocumentedMetadataExample
{
    public static async Task Use(ISharpLinkClient client, int id, CancellationToken cancellationToken)
    {
        {{codeBlock}}
    }
}
""";
        var compilation = CSharpCompilation.Create(
            "SharpLinkMetadataDocumentationCompilation",
            [CSharpSyntaxTree.ParseText(wrapper)],
            GeneratorTestHarness.GetPlatformReferences().Add(RealAbstractionsCompilationReference.Create()),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Ensure(errors.Length == 0, $"metadata docs must compile against the real client and metadata API: {FormatDiagnostics(errors)}");
        return Task.CompletedTask;
    }
}
