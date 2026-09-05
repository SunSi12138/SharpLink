namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private sealed partial class DtoAnalysisState
    {
        internal ImmutableArray<GeneratedCodecHashModel> BuildFinalCodecHashes(FinalCodecGraph graph)
        {
            graph = ApplySharpPackSidecarCodecIdentities(graph);
            var cache = new Dictionary<string, RpcHashValue>(StringComparer.Ordinal);
            return graph.Plans
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(pair =>
                {
                    var hash = HashCanonicalPlan(pair.Value, graph, cache, new HashSet<string>(StringComparer.Ordinal));
                    return new GeneratedCodecHashModel(
                        pair.Key,
                        hash.High,
                        hash.Low,
                        pair.Value is FinalReferencedCodecPlan);
                })
                .ToImmutableArray();
        }

        private static RpcHashValue HashCanonicalPlan(
            FinalCodecPlan plan,
            FinalCodecGraph graph,
            Dictionary<string, RpcHashValue> cache,
            HashSet<string> stack)
        {
            if (cache.TryGetValue(plan.TypeName, out var cached))
                return cached;
            if (!stack.Add(plan.TypeName))
            {
                throw new InvalidOperationException(
                    $"Resolved FinalCodecPlan graph contains a hash cycle at '{plan.TypeName}'.");
            }

            RpcHashValue hash = plan switch
            {
                FinalPrimitiveCodecPlan primitive => HashPrimitivePlan(primitive, graph, cache, stack),
                FinalEnumCodecPlan enumPlan => HashEnumPlan(enumPlan, graph, cache, stack),
                FinalGeneratedDtoCodecPlan dto => HashGeneratedDtoPlan(dto, graph, cache, stack),
                FinalUnionCodecPlan union => HashUnionPlan(union, graph, cache, stack),
                FinalCollectionCodecPlan collection => HashCollectionPlan(collection, graph, cache, stack),
                FinalUnsafeBlitCodecPlan unsafeBlit => HashUnsafeBlitPlan(unsafeBlit),
                FinalCustomCodecPlan custom => Hashing.GetSemanticHash(
                    "codec/v1",
                    "custom-closed/v1",
                    custom.OpaqueSemanticIdentity.ToHex(),
                    custom.ClosedTargetLogicalIdentity.ToHex()),
                FinalAdapterCodecPlan adapter => Hashing.GetSemanticHash(
                    "codec/v1",
                    "adapter-closed/v2",
                    adapter.OpaqueSemanticIdentity.ToHex(),
                    adapter.ClosedTargetLogicalIdentity.ToHex()),
                FinalReferencedCodecPlan referenced => referenced.CodecHash,
                _ => throw new InvalidOperationException(
                    $"Unknown resolved FinalCodecPlan '{plan.GetType().Name}'.")
            };

            stack.Remove(plan.TypeName);
            cache[plan.TypeName] = hash;
            return hash;
        }

        private static RpcHashValue HashPrimitivePlan(
            FinalPrimitiveCodecPlan plan,
            FinalCodecGraph graph,
            Dictionary<string, RpcHashValue> cache,
            HashSet<string> stack)
        {
            if (string.Equals(plan.Family, "nullable", StringComparison.Ordinal))
            {
                if (plan.ChildType is null)
                    throw new InvalidOperationException($"Nullable plan '{plan.TypeName}' has no child plan.");
                return Hashing.GetSemanticHash(
                    "codec/v1",
                    "nullable",
                    HashRequiredChild(plan.ChildType, graph, cache, stack).ToHex());
            }

            var parts = new List<string> { "codec/v1", plan.Family };
            parts.AddRange(plan.SemanticParts);
            if (plan.ChildType is not null)
                parts.Add(HashRequiredChild(plan.ChildType, graph, cache, stack).ToHex());
            return Hashing.GetSemanticHash(parts.ToArray());
        }

        private static RpcHashValue HashEnumPlan(
            FinalEnumCodecPlan plan,
            FinalCodecGraph graph,
            Dictionary<string, RpcHashValue> cache,
            HashSet<string> stack)
            => Hashing.GetSemanticHash(
                "codec/v1",
                "enum",
                HashRequiredChild(plan.UnderlyingType, graph, cache, stack).ToHex(),
                plan.DeclarationSemantic);

        private static RpcHashValue HashGeneratedDtoPlan(
            FinalGeneratedDtoCodecPlan plan,
            FinalCodecGraph graph,
            Dictionary<string, RpcHashValue> cache,
            HashSet<string> stack)
        {
            var parts = new List<string>
            {
                "codec/v1",
                "dto",
                plan.IsReferenceType ? "ref" : "value"
            };
            foreach (var member in plan.Members.OrderBy(static item => item.FieldId))
            {
                parts.Add(member.FieldId.ToString(InvariantCulture));
                parts.Add(member.Kind.ToString());
                parts.Add(member.Required ? "required" : "optional");
                parts.Add(member.Required && member.NonNullableReference
                    ? "required-non-null-ref"
                    : "no-required-reference-null-rejection");
                switch (member.WireStrategy)
                {
                    case FinalDtoMemberWireStrategy.String:
                        parts.Add("string/content/utf16le/i32le-byte-length/v1");
                        parts.Add("string/null/dto-wire-null/v1");
                        break;
                    case FinalDtoMemberWireStrategy.Fixed:
                        parts.Add(member.WireSemantic ?? throw new InvalidOperationException(
                            "Resolved fixed DTO member has no wire semantic."));
                        break;
                    case FinalDtoMemberWireStrategy.ChildCodec:
                        parts.Add(HashRequiredChild(
                            member.ChildType ?? throw new InvalidOperationException(
                                "Resolved complex DTO member has no child plan."),
                            graph,
                            cache,
                            stack).ToHex());
                        break;
                }
            }
            return Hashing.GetSemanticHash(parts.ToArray());
        }

        private static RpcHashValue HashUnionPlan(
            FinalUnionCodecPlan plan,
            FinalCodecGraph graph,
            Dictionary<string, RpcHashValue> cache,
            HashSet<string> stack)
        {
            var parts = new List<string>
            {
                "codec/v1",
                "union",
                plan.WireSemantic,
                plan.Cases.Length.ToString(InvariantCulture)
            };
            foreach (var unionCase in plan.Cases
                         .OrderBy(static item => item.Discriminator)
                         .ThenBy(static item => item.CaseLogicalIdentity.High)
                         .ThenBy(static item => item.CaseLogicalIdentity.Low))
            {
                parts.Add(unionCase.Discriminator.ToString(InvariantCulture));
                parts.Add(unionCase.CaseLogicalIdentity.ToHex());
                parts.Add(HashRequiredChild(unionCase.CaseTypeName, graph, cache, stack).ToHex());
            }
            return Hashing.GetSemanticHash(parts.ToArray());
        }

        private static RpcHashValue HashCollectionPlan(
            FinalCollectionCodecPlan plan,
            FinalCodecGraph graph,
            Dictionary<string, RpcHashValue> cache,
            HashSet<string> stack)
        {
            var parts = new List<string>
            {
                "codec/v1",
                "collection",
                plan.CollectionKind.ToString()
            };
            switch (plan.WireStrategy)
            {
                case FinalCollectionWireStrategy.ChildCodec:
                    AppendChild(plan.ElementType);
                    AppendChild(plan.KeyType);
                    AppendChild(plan.ValueType);
                    break;
                case FinalCollectionWireStrategy.RawBlit:
                    parts.Add(RequireStrategySemantic());
                    parts.Add(HashPhysicalLayout(
                        plan.RawElementLayout ?? throw new InvalidOperationException(
                            $"Raw-blit collection '{plan.TypeName}' has no physical element plan.")).ToHex());
                    break;
                case FinalCollectionWireStrategy.DateTimeOffsetCanonical:
                    parts.Add(RequireStrategySemantic());
                    break;
            }
            return Hashing.GetSemanticHash(parts.ToArray());

            string RequireStrategySemantic()
                => plan.StrategySemantic ?? throw new InvalidOperationException(
                    $"Resolved collection '{plan.TypeName}' has no wire strategy semantic.");

            void AppendChild(string? childType)
            {
                if (childType is not null)
                    parts.Add(HashRequiredChild(childType, graph, cache, stack).ToHex());
            }
        }

        private static RpcHashValue HashUnsafeBlitPlan(FinalUnsafeBlitCodecPlan plan)
            => Hashing.GetSemanticHash(
                "codec/v1",
                "unsafe-blit-plan/v3",
                "endianness:" + plan.Abi.Endianness,
                "native-pointer-width:" + plan.Abi.NativePointerWidth.ToString(InvariantCulture),
                "abi-version:" + plan.Abi.Version,
                HashPhysicalLayout(plan.Layout).ToHex());

        private static RpcHashValue HashRequiredChild(
            string childType,
            FinalCodecGraph graph,
            Dictionary<string, RpcHashValue> cache,
            HashSet<string> stack)
        {
            if (!graph.Plans.TryGetValue(childType, out var child))
            {
                throw new InvalidOperationException(
                    $"Resolved FinalCodecPlan graph is missing child '{childType}'.");
            }
            return HashCanonicalPlan(child, graph, cache, stack);
        }

        private static RpcHashValue HashPhysicalLayout(FinalPhysicalLayoutPlan plan)
        {
            switch (plan)
            {
                case FinalPrimitivePhysicalPlan primitive:
                    return Hashing.GetSemanticHash(
                        "physical/v1",
                        "primitive",
                        primitive.Token,
                        primitive.FrameworkRawAbi ?? string.Empty);
                case FinalEnumPhysicalPlan enumPlan:
                    return Hashing.GetSemanticHash(
                        "physical/v1",
                        "enum",
                        HashPhysicalLayout(enumPlan.Underlying).ToHex(),
                        enumPlan.DeclarationSemantic);
                case FinalPointerPhysicalPlan pointer:
                    return Hashing.GetSemanticHash(
                        "physical/v1",
                        "native-pointer",
                        pointer.TargetLogicalIdentity);
                case FinalFunctionPointerPhysicalPlan functionPointer:
                    return Hashing.GetSemanticHash(
                        "physical/v1",
                        "function-pointer",
                        functionPointer.SignatureSemantic);
                case FinalFixedBufferPhysicalPlan fixedBuffer:
                    return Hashing.GetSemanticHash(
                        "physical/v1",
                        "fixed-buffer",
                        fixedBuffer.Length.ToString(InvariantCulture),
                        HashPhysicalLayout(fixedBuffer.Element).ToHex());
                case FinalStructPhysicalPlan structure:
                    {
                        var parts = new List<string>
                        {
                            "physical/v1",
                            "struct",
                            structure.LayoutKind.ToString(),
                            structure.Pack.ToString(InvariantCulture),
                            structure.Size.ToString(InvariantCulture),
                            structure.InlineArrayLength?.ToString(InvariantCulture) ?? string.Empty,
                            structure.Fields.Length.ToString(InvariantCulture)
                        };
                        foreach (var field in structure.Fields)
                        {
                            parts.Add(field.Offset?.ToString(InvariantCulture) ?? "sequential");
                            parts.Add(HashPhysicalLayout(field.Layout).ToHex());
                        }
                        return Hashing.GetSemanticHash(parts.ToArray());
                    }
                default:
                    throw new InvalidOperationException(
                        $"Unknown resolved physical plan '{plan.GetType().Name}'.");
            }
        }
    }
}
