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
    public Task Issue903DocumentedSingleMetadataEntryMustCompileAgainstActualMetadataSource()
    {
        var markdown = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "doc-calls-and-streaming.md"));
        var codeBlock = markdown.Split("```csharp", StringSplitOptions.None)
            .Skip(1)
            .Select(part => part.Split("```", StringSplitOptions.None)[0])
            .Single(block => block.Contains("CreateProxy<IMyService>", StringComparison.Ordinal));
        var metadataSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SharpLinkMetadata.source.cs"));
        var wrapper = $$"""
using System;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

public interface IMyService { Task<int> GetAsync(int id, CancellationToken token); }
public sealed class Client
{
    public RpcProxy<T> CreateProxy<T>(RpcProxyOptions options) where T : class
        => throw new NotSupportedException();
}
public sealed class RpcProxyOptions
{
    public SharpLinkMetadata Metadata { get; init; } = null!;
}
public sealed class RpcProxy<T> where T : class
{
    public T Service => throw new NotSupportedException();
    public ValueTask PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
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
                CSharpSyntaxTree.ParseText(metadataSource),
                CSharpSyntaxTree.ParseText(wrapper)
            ],
            GeneratorTestHarness.GetPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Ensure(errors.Length == 0, $"metadata docs must compile against real SharpLinkMetadata.cs: {FormatDiagnostics(errors)}");
        return Task.CompletedTask;
    }
}
