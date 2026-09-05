using System;
using System.Collections;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task SharedBuiltinCollectionCatalogShouldRemainRuntimeSelectedInGeneratorAnalysis()
    {
        var catalogType = typeof(RpcGenerator).Assembly.GetType(
            "SharpLink.RpcBuiltinCollectionWireCatalog",
            throwOnError: true)!;
        var allProperty = catalogType.GetProperty(
            "All",
            BindingFlags.Static | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Shared builtin collection catalog has no All property.");
        var descriptors = allProperty.GetValue(null) as IEnumerable ??
            throw new InvalidOperationException("Shared builtin collection catalog has an unexpected All value.");

        var methods = new StringBuilder();
        var index = 0;
        foreach (var descriptor in descriptors)
        {
            var elementTypeName = descriptor!.GetType().GetProperty("ElementTypeName")?.GetValue(descriptor) as string ??
                throw new InvalidOperationException("Builtin collection descriptor has no element type name.");
            var elementType = "global::" + elementTypeName;
            var listType = $"global::System.Collections.Generic.List<{elementType}>";
            methods.Append("    global::System.Threading.Tasks.ValueTask<")
                .Append(listType)
                .Append("> Echo")
                .Append(index++)
                .Append('(')
                .Append(listType)
                .Append(" value, global::System.Threading.CancellationToken cancellationToken);\n");
        }

        var source = BuildSource($$"""
[SharpLink.Sdk.RpcContract]
public interface IBuiltinCollectionCatalogContract : SharpLink.Sdk.IService
{
{{methods}}}
""");
        AssertResolvedManifest(source, "shared builtin collection catalog");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(
            !generated.Contains(
                "TargetType => typeof(global::System.Collections.Generic.List<",
                StringComparison.Ordinal),
            "runtime-selected builtin List<T> shapes must not be emitted as generated collection Codec factories");
        return Task.CompletedTask;
    }
}
