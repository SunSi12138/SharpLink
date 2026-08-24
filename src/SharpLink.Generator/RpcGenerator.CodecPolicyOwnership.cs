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

        // Contract-reachable types keep their default/native Codec in the global provider even when
        // the same source type is also [RpcSerializable]. Compile-time Contract policy is an owner
        // delta and must not replace runtime/default provider configuration for that CLR type.
        var globalByType = standalone.Codecs.ToDictionary(static codec => codec.TypeName, StringComparer.Ordinal);
        foreach (var codec in contractDefault.Codecs)
            globalByType[codec.TypeName] = codec;
        var globalCodecs = globalByType.Values
            .OrderBy(static codec => codec.TypeName, StringComparer.Ordinal)
            .ToImmutableArray();

        var contractCodecs = SelectOwnedContractCodecs(globalCodecs, contractPolicy.Codecs);
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
        ImmutableArray<GeneratedCodecModel> globalCodecs,
        ImmutableArray<GeneratedCodecModel> contractPolicy)
    {
        var globalByType = globalCodecs.ToDictionary(static codec => codec.TypeName, StringComparer.Ordinal);
        var scopedTypes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var codec in contractPolicy)
        {
            if (!globalByType.TryGetValue(codec.TypeName, out var global) ||
                !HasSameCodecDefinition(global, codec))
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
        if (implementation is null || ImplementsRpcCodecAdapter(implementation) ||
            implementation.AllInterfaces.Any(static item =>
                item.Name == "IRpcCodec" &&
                item.Arity == 1 &&
                item.ContainingNamespace.ToDisplayString() == "SharpLink.Abstractions"))
        {
            return diagnostic;
        }

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
