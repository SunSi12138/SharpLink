namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private sealed partial class DtoAnalysisState
    {
        private static readonly FinalUnsafeBlitAbiPlan UnsafeBlitAbi =
            new("little-endian", 8, "v3");


        internal FinalCodecGraph ResolveFinalCodecGraph(
            bool includeSerializable,
            bool includeContracts)
        {
            var roots = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
            CollectCurrentAssemblyRoots(
                _compilation.Assembly.GlobalNamespace,
                roots,
                includeSerializable,
                includeContracts);

            var plans = new Dictionary<string, FinalCodecPlan>(StringComparer.Ordinal);
            var resolving = new HashSet<string>(StringComparer.Ordinal);

            // Native union nodes are immutable declarations over exact child type references.
            // Seed every reached union before resolving any children so nested unions resolve to
            // the same final node instead of depending on declaration or traversal order.
            foreach (var model in _models.Values
                         .Where(static model => model.Kind == GeneratedCodecKind.Union)
                         .OrderBy(static model => model.TypeName, StringComparer.Ordinal))
            {
                if (!_nativeUnionTypes.TryGetValue(model.TypeName, out var unionType))
                {
                    throw new InvalidOperationException(
                        $"Native union candidate '{model.TypeName}' has no Roslyn declaration symbol.");
                }
                plans[model.TypeName] = CreateFinalUnionPlan(unionType, model);
            }

            // Materialize every union case child before resolving roots. A case DTO may itself
            // reference another pre-seeded union; this remains deterministic and cycle-safe.
            foreach (var model in _models.Values
                         .Where(static model => model.Kind == GeneratedCodecKind.Union)
                         .OrderBy(static model => model.TypeName, StringComparer.Ordinal))
            {
                var unionType = _nativeUnionTypes[model.TypeName];
                if (!TryGetNativeUnionCases(unionType, reportDiagnostics: false, out var cases))
                {
                    throw new InvalidOperationException(
                        $"Native union candidate '{model.TypeName}' lost its declaration cases during final graph resolution.");
                }
                foreach (var unionCase in cases)
                {
                    if (ResolveFinalCodecPlan(unionCase.Type, plans, resolving) is null)
                        _failed.Add(model.TypeName);
                }
            }
            PruneFailedFinalPlanClosure(plans);

            foreach (var pair in roots.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                if (!_failed.Contains(pair.Key))
                    ResolveFinalCodecPlan(pair.Value, plans, resolving);
            }
            PruneFailedFinalPlanClosure(plans);

            // Enum declaration semantics can be required by generated metadata even when the
            // enclosing runtime Codec is a raw physical plan such as UnsafeBlit<Nullable<TEnum>>.
            // Materialize those reached enum nodes here so every downstream consumer observes the
            // same complete final graph without consulting Roslyn again.
            foreach (var enumModel in _enums.Values.OrderBy(static item => item.TypeName, StringComparer.Ordinal))
            {
                if (_failed.Contains(enumModel.TypeName) || plans.ContainsKey(enumModel.TypeName))
                    continue;
                if (!TryResolveReachableType(enumModel.TypeName, out var type) ||
                    type is not INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
                {
                    throw new InvalidOperationException(
                        $"Final RPC Codec graph cannot resolve reached enum metadata for '{enumModel.TypeName}'.");
                }
                ResolveFinalCodecPlan(enumType, plans, resolving);
            }

            return new FinalCodecGraph(
                plans,
                roots.Keys.Where(type => !_failed.Contains(type))
                    .OrderBy(static type => type, StringComparer.Ordinal)
                    .ToImmutableArray());
        }

        private void PruneFailedFinalPlanClosure(Dictionary<string, FinalCodecPlan> plans)
        {
            bool changed;
            do
            {
                changed = false;
                foreach (var plan in plans.Values.ToArray())
                {
                    if (_failed.Contains(plan.TypeName) ||
                        GetFinalCodecDependenciesIncludingUnion(plan).Any(_failed.Contains))
                    {
                        changed |= _failed.Add(plan.TypeName);
                    }
                }
            }
            while (changed);

            foreach (var failedType in _failed)
                plans.Remove(failedType);
        }
    }
}
