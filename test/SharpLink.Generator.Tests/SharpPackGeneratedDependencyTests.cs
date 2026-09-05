using System;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public void CurrentSharpPackableContextDependenciesShouldGenerateExternalSidecar()
    {
        var vendor = CreateSharpPackVendorReference("""
namespace Vendor;

public sealed class ExternalChild
{
    public int Id { get; set; }
    public string? Name { get; set; }
}
""");
        var source = BuildSharpPackContractSource(
            """
    global::System.Threading.Tasks.Task<SourceSharpPackRoot> EchoAsync(
        SourceSharpPackRoot request,
        global::System.Threading.CancellationToken cancellationToken);
""",
            """
[global::SharpPack.SharpPackable]
public partial class SourceSharpPackRoot
{
    [global::SharpPack.SharpPackAllowSerialize]
    public global::Vendor.ExternalChild? Child { get; set; }

    public global::System.Collections.Generic.List<global::Vendor.ExternalChild>? Children { get; set; }
}
""");

        var result = RunSharpPackAndCompile(
            "SharpPackCurrentGeneratedDependency",
            source,
            [vendor]);
        EnsureNoSharpPackErrors(result);
        var generated = GetSharpPackGeneratedSource(result.DriverRunResult);

        Ensure(generated.Contains(
                "SharpPackFormatter<global::Vendor.ExternalChild>",
                StringComparison.Ordinal),
            "context-resolved external child receives a generated sidecar");
        Ensure(generated.Contains(
                "builder.Register<global::Vendor.ExternalChild>",
                StringComparison.Ordinal),
            "external child sidecar is registered into the generated SharpPack scope");
        Ensure(!generated.Contains(
                "SharpPackFormatter<global::SourceSharpPackRoot>",
                StringComparison.Ordinal),
            "the current-compilation SharpPack-generated root remains owned by SharpPack");
    }

    [Test]
    public void SharpPackGeneratedCollectionShouldIgnoreUnrelatedGenericInterfaces()
    {
        var source = BuildSharpPackContractSource(
            """
    global::System.Threading.Tasks.Task<SourceSharpPackCollection> EchoAsync(
        SourceSharpPackCollection request,
        global::System.Threading.CancellationToken cancellationToken);
""",
            """
[global::SharpPack.SharpPackable(global::SharpPack.GenerateType.Collection)]
public partial class SourceSharpPackCollection :
    global::System.Collections.Generic.ICollection<int>,
    global::System.IEquatable<object>
{
    private readonly global::System.Collections.Generic.List<int> _items = new();

    public int Count => _items.Count;
    public bool IsReadOnly => false;

    public void Add(int item) => _items.Add(item);
    public void Clear() => _items.Clear();
    public bool Contains(int item) => _items.Contains(item);
    public void CopyTo(int[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    public bool Remove(int item) => _items.Remove(item);
    public global::System.Collections.Generic.IEnumerator<int> GetEnumerator() => _items.GetEnumerator();
    global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public bool Equals(object? other) => global::System.Object.ReferenceEquals(this, other);
}
""");

        var result = RunSharpPackAndCompile(
            "SharpPackCollectionDependencySelection",
            source,
            []);
        EnsureNoSharpPackErrors(result);
        var generated = GetSharpPackGeneratedSource(result.DriverRunResult);

        Ensure(!generated.Contains(
                "builder.Register<object>",
                StringComparison.Ordinal),
            "unrelated IEquatable<object> must not become a SharpPack collection dependency");
        Ensure(!generated.Contains(
                "SharpPackFormatter<object>",
                StringComparison.Ordinal),
            "unrelated generic-interface arguments must not receive sidecars");
    }

    [Test]
    public void SharpPackExternalUnionFormatterShouldOwnNoGenerateTargetAndAnalyzeTags()
    {
        var vendor = CreateSharpPackVendorReference("""
namespace Vendor;

public sealed class ExternalChild
{
    public int Id { get; set; }
    public string? Name { get; set; }
}
""");
        var source = BuildSharpPackContractSource(
            """
    global::System.Threading.Tasks.Task<ISourceExternalUnion> EchoAsync(
        ISourceExternalUnion request,
        global::System.Threading.CancellationToken cancellationToken);
""",
            """
[global::SharpPack.SharpPackable(global::SharpPack.GenerateType.NoGenerate)]
public partial interface ISourceExternalUnion
{
}

[global::SharpPack.SharpPackable]
public partial class SourceExternalUnionValue : ISourceExternalUnion
{
    [global::SharpPack.SharpPackAllowSerialize]
    public global::Vendor.ExternalChild? Child { get; set; }
}

[global::SharpPack.SharpPackUnionFormatter(typeof(ISourceExternalUnion))]
[global::SharpPack.SharpPackUnion(7, typeof(SourceExternalUnionValue))]
public partial class SourceExternalUnionFormatter
{
}
""");

        var result = RunSharpPackAndCompile(
            "SharpPackExternalUnionGeneratedDependency",
            source,
            [vendor]);
        EnsureNoSharpPackErrors(result);
        var generated = GetSharpPackGeneratedSource(result.DriverRunResult);

        Ensure(!generated.Contains(
                "SharpPackFormatter<global::ISourceExternalUnion>",
                StringComparison.Ordinal),
            "NoGenerate external-union target remains owned by SharpPack's generated factory");
        Ensure(generated.Contains(
                "SharpPackFormatter<global::Vendor.ExternalChild>",
                StringComparison.Ordinal),
            "external-union tag graph must continue into context-resolved external children");
        Ensure(generated.Contains(
                "builder.Register<global::Vendor.ExternalChild>",
                StringComparison.Ordinal),
            "external-union nested external child sidecar is registered into the generated scope");
    }
}
