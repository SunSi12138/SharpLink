namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static DtoGenerationResult AnalyzeGeneratedCodecsWithPolicyOwnership(
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        var standalone = new DtoAnalysisState(
            compilation,
            cancellationToken,
            contractMode: false,
            applyCodecPolicy: true).Analyze();
        var contractDefault = new DtoAnalysisState(
            compilation,
            cancellationToken,
            contractMode: true,
            applyCodecPolicy: false).Analyze();
        var contractPolicy = new DtoAnalysisState(
            compilation,
            cancellationToken,
            contractMode: true,
            applyCodecPolicy: true).Analyze();

        // Only true standalone publication belongs in the context-global generated registry. A type
        // that is both [RpcSerializable] and Contract-reachable keeps its default/native standalone
        // binding there; Contract-only default codecs stay implicit so runtime UseCodec<T> keeps its
        // established no-policy precedence.
        var globalByType = standalone.Codecs.ToDictionary(static codec => codec.TypeName, StringComparer.Ordinal);
        foreach (var codec in contractDefault.Codecs)
        {
            if (globalByType.ContainsKey(codec.TypeName))
                globalByType[codec.TypeName] = codec;
        }
        var globalCodecs = globalByType.Values
            .OrderBy(static codec => codec.TypeName, StringComparer.Ordinal)
            .ToImmutableArray();

        // Contract ownership is defined by the delta from the Contract's default graph, not by
        // whether the type happens to be globally published. This leaves no-policy Contracts on the
        // base provider while pulling in native parents/dependencies that must close over a changed
        // explicit/route binding.
        var contractCodecs = SelectOwnedContractCodecs(contractDefault.Codecs, contractPolicy.Codecs);
        var diagnostics = standalone.Diagnostics
            .Concat(contractPolicy.Diagnostics)
            .Select(diagnostic => NormalizeExplicitBindingDiagnostic(compilation, diagnostic, cancellationToken))
            .GroupBy(static item => (item.Kind, item.TypeName, item.Detail))
            .Select(static group => group.First())
            .ToImmutableArray();
        var enums = standalone.Enums
            .Concat(contractDefault.Enums)
            .Concat(contractPolicy.Enums)
            .GroupBy(static item => item.TypeName, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static item => item.TypeName, StringComparer.Ordinal)
            .ToImmutableArray();

        return new DtoGenerationResult(
            globalCodecs,
            contractCodecs,
            contractPolicy.Codecs,
            diagnostics,
            enums);
    }

    private static ImmutableArray<GeneratedCodecModel> SelectOwnedContractCodecs(
        ImmutableArray<GeneratedCodecModel> contractDefault,
        ImmutableArray<GeneratedCodecModel> contractPolicy)
    {
        var defaultByType = contractDefault.ToDictionary(static codec => codec.TypeName, StringComparer.Ordinal);
        var scopedTypes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var codec in contractPolicy)
        {
            if (!defaultByType.TryGetValue(codec.TypeName, out var defaultCodec) ||
                !HasSameCodecDefinition(defaultCodec, codec))
            {
                scopedTypes.Add(codec.TypeName);
            }
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var codec in contractPolicy)
            {
                if (scopedTypes.Contains(codec.TypeName))
                    continue;
                if (GetCodecDependencies(codec).Any(scopedTypes.Contains))
                    changed |= scopedTypes.Add(codec.TypeName);
            }
        }
        while (changed);

        return contractPolicy
            .Where(codec => scopedTypes.Contains(codec.TypeName))
            .Select(static codec => codec with
            {
                // The global/default and Contract-policy versions of one type can coexist in one
                // generated source file, so policy factories need a distinct generated type name.
                CodecName = "__SharpLinkGeneratedContractPolicyCodec_" +
                            Hashing.GetIdentifierHash("contract-policy|" + codec.TypeName)
            })
            .OrderBy(static codec => codec.TypeName, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static DtoDiagnosticModel NormalizeExplicitBindingDiagnostic(
        Compilation compilation,
        DtoDiagnosticModel diagnostic,
        CancellationToken cancellationToken)
    {
        if (diagnostic.Kind != DtoDiagnosticKind.AdapterTypeInvalid ||
            !diagnostic.Detail.StartsWith("direct Codec must", StringComparison.Ordinal))
        {
            return diagnostic;
        }

        var metadataName = diagnostic.TypeName.StartsWith("global::", StringComparison.Ordinal)
            ? diagnostic.TypeName.Substring("global::".Length)
            : diagnostic.TypeName;
        var implementation = compilation.GetTypeByMetadataName(metadataName);
        if (implementation is null)
            return diagnostic;

        var implementsAdapter = implementation.AllInterfaces.Any(static item =>
            item.Name == "IRpcCodecAdapter" &&
            item.ContainingNamespace.ToDisplayString() == "SharpLink.Abstractions");
        var implementsCodec = implementation.AllInterfaces.Any(static item =>
            item.Name == "IRpcCodec" &&
            item.Arity == 1 &&
            item.ContainingNamespace.ToDisplayString() == "SharpLink.Abstractions");
        if (implementsAdapter || implementsCodec)
            return diagnostic;

        // Before direct Codec support, a plain class selected by RpcCodecAdapter meant "adapter that
        // forgot registration". Preserve that diagnostic unless WireFormatId makes direct-Codec
        // intent explicit.
        if (diagnostic.Location?.SourceTree is { } tree)
        {
            var source = tree.GetText(cancellationToken).ToString(diagnostic.Location.SourceSpan);
            if (source.Contains("WireFormatId", StringComparison.Ordinal))
                return diagnostic;
        }

        return diagnostic with
        {
            Kind = DtoDiagnosticKind.AdapterRegistrationInvalid,
            Detail = $"selected Adapter '{diagnostic.TypeName}' has no valid RpcCodecAdapterRegistration"
        };
    }
}