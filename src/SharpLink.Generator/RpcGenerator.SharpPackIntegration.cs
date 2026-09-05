namespace SharpLink.Generator;

internal sealed record SharpPackSidecarMemberModel(
    string Name,
    string Identifier,
    string TypeName,
    int Order);

internal sealed record SharpPackSidecarModel(
    string TypeName,
    string FormatterName,
    bool IsReferenceType,
    ImmutableArray<SharpPackSidecarMemberModel> Members,
    ImmutableArray<string> ConstructorMembers);

internal readonly record struct SharpPackIntegrationDiagnosticModel(
    string TypeName,
    string Detail,
    Location? Location);

internal sealed record SharpPackIntegrationAnalysisResult(
    ImmutableArray<SharpPackSidecarModel> Sidecars,
    ImmutableArray<SharpPackIntegrationDiagnosticModel> Diagnostics,
    bool HasBindings)
{
    internal static SharpPackIntegrationAnalysisResult Empty { get; } = new(
        ImmutableArray<SharpPackSidecarModel>.Empty,
        ImmutableArray<SharpPackIntegrationDiagnosticModel>.Empty,
        HasBindings: false);
}

public partial class RpcGenerator
{
    private const string SharpPackAdapterId = "sharplink.serializer.sharppack/v1";
    private const string SharpPackAdapterTypeName =
        "global::SharpLink.Serializer.SharpPack.SharpPackRpcCodecAdapter";

    private static readonly HashSet<string> SharpPackWellKnownManagedTypes = new(StringComparer.Ordinal)
    {
        "System.String",
        "System.Version",
        "System.Uri",
        "System.TimeZoneInfo",
        "System.Numerics.BigInteger",
        "System.Collections.BitArray",
        "System.Text.StringBuilder",
        "System.Type",
        "System.Globalization.CultureInfo"
    };

    private static readonly HashSet<string> SharpPackKnownGenericTypes = new(StringComparer.Ordinal)
    {
        "System.Collections.Generic.KeyValuePair`2",
        "System.Lazy`1",
        "System.Nullable`1",
        "System.ArraySegment`1",
        "System.Memory`1",
        "System.ReadOnlyMemory`1",
        "System.Buffers.ReadOnlySequence`1",
        "System.Collections.Generic.List`1",
        "System.Collections.Generic.Stack`1",
        "System.Collections.Generic.Queue`1",
        "System.Collections.Generic.LinkedList`1",
        "System.Collections.Generic.HashSet`1",
        "System.Collections.Generic.SortedSet`1",
        "System.Collections.Generic.PriorityQueue`2",
        "System.Collections.ObjectModel.ObservableCollection`1",
        "System.Collections.ObjectModel.Collection`1",
        "System.Collections.Concurrent.ConcurrentQueue`1",
        "System.Collections.Concurrent.ConcurrentStack`1",
        "System.Collections.Concurrent.ConcurrentBag`1",
        "System.Collections.Generic.Dictionary`2",
        "System.Collections.Generic.SortedDictionary`2",
        "System.Collections.Generic.SortedList`2",
        "System.Collections.Concurrent.ConcurrentDictionary`2",
        "System.Collections.ObjectModel.ReadOnlyCollection`1",
        "System.Collections.ObjectModel.ReadOnlyObservableCollection`1",
        "System.Collections.Concurrent.BlockingCollection`1",
        "System.Collections.Generic.IEnumerable`1",
        "System.Collections.Generic.ICollection`1",
        "System.Collections.Generic.IReadOnlyCollection`1",
        "System.Collections.Generic.IList`1",
        "System.Collections.Generic.IReadOnlyList`1",
        "System.Collections.Generic.IDictionary`2",
        "System.Collections.Generic.IReadOnlyDictionary`2",
        "System.Linq.ILookup`2",
        "System.Linq.IGrouping`2",
        "System.Collections.Generic.ISet`1",
        "System.Collections.Generic.IReadOnlySet`1",
        "System.Collections.Immutable.ImmutableArray`1",
        "System.Collections.Immutable.ImmutableList`1",
        "System.Collections.Immutable.ImmutableQueue`1",
        "System.Collections.Immutable.ImmutableStack`1",
        "System.Collections.Immutable.ImmutableDictionary`2",
        "System.Collections.Immutable.ImmutableSortedDictionary`2",
        "System.Collections.Immutable.ImmutableSortedSet`1",
        "System.Collections.Immutable.ImmutableHashSet`1",
        "System.Collections.Immutable.IImmutableList`1",
        "System.Collections.Immutable.IImmutableQueue`1",
        "System.Collections.Immutable.IImmutableStack`1",
        "System.Collections.Immutable.IImmutableDictionary`2",
        "System.Collections.Immutable.IImmutableSet`1",
        "System.Collections.Frozen.FrozenDictionary`2",
        "System.Collections.Frozen.FrozenSet`1",
        "System.Tuple`1",
        "System.Tuple`2",
        "System.Tuple`3",
        "System.Tuple`4",
        "System.Tuple`5",
        "System.Tuple`6",
        "System.Tuple`7",
        "System.Tuple`8",
        "System.ValueTuple`1",
        "System.ValueTuple`2",
        "System.ValueTuple`3",
        "System.ValueTuple`4",
        "System.ValueTuple`5",
        "System.ValueTuple`6",
        "System.ValueTuple`7",
        "System.ValueTuple`8"
    };

    private static SharpPackIntegrationAnalysisResult AnalyzeSharpPackIntegration(
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        if (compilation.GetTypeByMetadataName("SharpPack.SharpPackFormatter`1") is null ||
            compilation.GetTypeByMetadataName(
                "SharpLink.Serializer.SharpPack.SharpPackRpcCodecAdapter") is null)
        {
            return SharpPackIntegrationAnalysisResult.Empty;
        }

        var standaloneState = new DtoAnalysisState(
            compilation,
            cancellationToken,
            contractMode: false,
            applyCodecPolicy: true,
            selectorOnlyContractDefault: false);
        _ = standaloneState.AnalyzeWithFinalCodecBindings();
        var standaloneGraph = standaloneState.ResolveFinalCodecGraph(
            includeSerializable: true,
            includeContracts: false);
        var standalone = AnalyzeSharpPackBindings(compilation, standaloneState, standaloneGraph);

        var contractState = new DtoAnalysisState(
            compilation,
            cancellationToken,
            contractMode: true,
            applyCodecPolicy: true,
            selectorOnlyContractDefault: false);
        _ = contractState.AnalyzeWithFinalCodecBindings();
        var contractGraph = contractState.ResolveFinalCodecGraph(
            includeSerializable: false,
            includeContracts: true);
        var contract = AnalyzeSharpPackBindings(compilation, contractState, contractGraph);

        var sidecars = standalone.Sidecars
            .Concat(contract.Sidecars)
            .GroupBy(static item => item.TypeName, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static item => item.TypeName, StringComparer.Ordinal)
            .ToImmutableArray();
        var diagnostics = standalone.Diagnostics
            .Concat(contract.Diagnostics)
            .GroupBy(static item => (item.TypeName, item.Detail))
            .Select(static group => group.First())
            .OrderBy(static item => item.TypeName, StringComparer.Ordinal)
            .ThenBy(static item => item.Detail, StringComparer.Ordinal)
            .ToImmutableArray();

        return new SharpPackIntegrationAnalysisResult(
            sidecars,
            diagnostics,
            standalone.HasBindings || contract.HasBindings);
    }

    private static SharpPackIntegrationAnalysisResult AnalyzeSharpPackBindings(
        Compilation compilation,
        DtoAnalysisState state,
        FinalCodecGraph graph)
    {
        var rootPlans = graph.Plans.Values
            .OfType<FinalAdapterCodecPlan>()
            .Where(static plan =>
                string.Equals(plan.AdapterId, SharpPackAdapterId, StringComparison.Ordinal) &&
                string.Equals(plan.AdapterTypeName, SharpPackAdapterTypeName, StringComparison.Ordinal))
            .OrderBy(static plan => plan.TypeName, StringComparer.Ordinal)
            .ToImmutableArray();
        if (rootPlans.IsDefaultOrEmpty)
            return SharpPackIntegrationAnalysisResult.Empty;

        var analysis = new SharpPackSidecarAnalysis(compilation);
        foreach (var root in rootPlans)
        {
            if (!state.TryResolveSharpPackReachableType(root.TypeName, out var rootType))
            {
                analysis.Report(
                    root.TypeName,
                    $"wire root '{root.TypeName}' cannot be resolved from the closed Contract payload graph",
                    Location.None);
                continue;
            }

            analysis.AnalyzeRoot(rootType);
        }

        return analysis.ToResult();
    }

    private static bool IsSharpPackAdapter(GeneratedCodecModel model)
        => model.Kind == GeneratedCodecKind.Adapter &&
           string.Equals(model.AdapterId, SharpPackAdapterId, StringComparison.Ordinal) &&
           string.Equals(model.AdapterType, SharpPackAdapterTypeName, StringComparison.Ordinal);
}
