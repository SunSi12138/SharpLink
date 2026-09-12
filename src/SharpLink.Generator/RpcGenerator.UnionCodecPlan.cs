namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private sealed partial class DtoAnalysisState
    {
        private const string UnionWireSemantic = "union/discriminator/i32le/null-zero/v1";

        private bool HasNativeUnionDeclaration(ITypeSymbol type)
        {
            var assembly = type.ContainingAssembly;
            if (assembly is not null &&
                !SymbolEqualityComparer.Default.Equals(assembly, _compilation.Assembly) &&
                HasReferencedGeneratedCodecIdentityCandidate(type))
            {
                return false;
            }

            return type.GetAttributes().Any(static attribute =>
                IsAttribute(attribute, "SharpLink.Sdk", "RpcUnionCaseAttribute"));
        }

        private bool IsRuntimeCompatibleUnionCase(INamedTypeSymbol unionType, INamedTypeSymbol caseType)
        {
            if (IsDirectUnionCase(unionType, caseType))
                return true;
            if (_compilation is not Microsoft.CodeAnalysis.CSharp.CSharpCompilation csharpCompilation)
                return false;
            var conversion = csharpCompilation.ClassifyConversion(caseType, unionType);
            return conversion.IsImplicit &&
                   (conversion.IsIdentity || conversion.IsReference || conversion.IsBoxing);
        }

        private FinalUnionCodecPlan? ResolveNativeUnionCodecPlan(
            ITypeSymbol type,
            Dictionary<string, FinalCodecPlan> plans,
            HashSet<string> resolving)
        {
            var typeName = GetTypeName(type);
            if (type is not INamedTypeSymbol unionType ||
                unionType.TypeKind is not (TypeKind.Class or TypeKind.Interface) ||
                HasTypeParameter(unionType))
            {
                Report(DtoDiagnosticKind.Unsupported, type,
                    "native union declarations must target a closed class or interface");
                _failed.Add(typeName);
                return null;
            }

            var attributes = unionType.GetAttributes()
                .Where(static attribute => IsAttribute(attribute, "SharpLink.Sdk", "RpcUnionCaseAttribute"))
                .ToArray();
            if (attributes.Length == 0)
                return null;

            var cases = new List<NativeUnionCase>(attributes.Length);
            var tags = new Dictionary<int, NativeUnionCase>();
            var caseTypes = new Dictionary<string, NativeUnionCase>(StringComparer.Ordinal);
            var invalid = false;
            foreach (var attribute in attributes)
            {
                var location = attribute.ApplicationSyntaxReference?.GetSyntax(_cancellationToken).GetLocation()
                    ?? unionType.Locations.FirstOrDefault();
                if (attribute.ConstructorArguments.Length != 2 ||
                    attribute.ConstructorArguments[0].Value is not int tag ||
                    attribute.ConstructorArguments[1].Value is not ITypeSymbol rawCaseType)
                {
                    Report(DtoDiagnosticKind.Unsupported, unionType,
                        "RpcUnionCase requires a positive discriminator and one closed concrete case type",
                        location);
                    invalid = true;
                    continue;
                }

                var detail = GetInvalidUnionCaseDetail(unionType, tag, rawCaseType, _compilation);
                if (detail is not null || rawCaseType is not INamedTypeSymbol caseType)
                {
                    Report(DtoDiagnosticKind.Unsupported, unionType,
                        detail ?? "RpcUnionCase case type is invalid", location);
                    invalid = true;
                    continue;
                }
                if (SymbolEqualityComparer.Default.Equals(unionType, caseType))
                {
                    Report(DtoDiagnosticKind.Unsupported, unionType,
                        $"native union '{typeName}' cannot declare itself as a case; native union case Codec dependencies must be acyclic",
                        location);
                    invalid = true;
                    continue;
                }
                if (!IsRuntimeCompatibleUnionCase(unionType, caseType))
                {
                    Report(DtoDiagnosticKind.Unsupported, unionType,
                        $"native union case '{GetTypeName(caseType)}' must be assignable by an identity, reference, or boxing conversion; user-defined conversions cannot define runtime union cases",
                        location);
                    invalid = true;
                    continue;
                }
                if (caseType.TypeKind == TypeKind.Class && !caseType.IsSealed)
                {
                    Report(DtoDiagnosticKind.Unsupported, unionType,
                        $"native union case '{GetTypeName(caseType)}' must be sealed to guarantee fail-closed runtime dispatch; use a sealed case type or bind an explicit typed Codec or Codec Adapter",
                        location);
                    invalid = true;
                }
                if (caseType.IsUnmanagedType &&
                    IsRuntimeSizedUnsafeBlitType(caseType) &&
                    !HasCodecPolicyCandidate(caseType) &&
                    !HasReferencedGeneratedCodecIdentityCandidate(caseType))
                {
                    Report(DtoDiagnosticKind.Unsupported, unionType,
                        $"union case '{GetTypeName(caseType)}' is a runtime-sized unmanaged type and requires an explicit typed Codec or Codec Adapter",
                        location);
                    invalid = true;
                    continue;
                }

                var item = new NativeUnionCase(tag, caseType, location);
                if (tags.TryGetValue(tag, out var existingTag))
                {
                    Report(DtoDiagnosticKind.Unsupported, unionType,
                        $"union discriminator {tag} maps to both '{GetTypeName(existingTag.CaseType)}' and '{GetTypeName(caseType)}'",
                        location);
                    invalid = true;
                }
                else
                {
                    tags.Add(tag, item);
                }

                var caseTypeName = GetTypeName(caseType);
                if (caseTypes.TryGetValue(caseTypeName, out var existingCase))
                {
                    Report(DtoDiagnosticKind.Unsupported, unionType,
                        $"union case '{caseTypeName}' is assigned both discriminator {existingCase.Discriminator} and {tag}",
                        location);
                    invalid = true;
                }
                else
                {
                    caseTypes.Add(caseTypeName, item);
                }
                cases.Add(item);
            }

            for (var leftIndex = 0; leftIndex < cases.Count; leftIndex++)
            {
                for (var rightIndex = leftIndex + 1; rightIndex < cases.Count; rightIndex++)
                {
                    var left = cases[leftIndex].CaseType;
                    var right = cases[rightIndex].CaseType;
                    if (SymbolEqualityComparer.Default.Equals(left, right))
                        continue;
                    if (!IsDirectUnionCase(left, right) && !IsDirectUnionCase(right, left))
                        continue;
                    Report(DtoDiagnosticKind.Unsupported, unionType,
                        $"union cases '{GetTypeName(left)}' and '{GetTypeName(right)}' overlap at runtime; declared cases must be mutually exclusive",
                        cases[rightIndex].Location);
                    invalid = true;
                }
            }

            if (invalid)
            {
                _failed.Add(typeName);
                return null;
            }

            var orderedCases = cases
                .OrderBy(static item => item.Discriminator)
                .ThenBy(static item => GetTypeName(item.CaseType), StringComparer.Ordinal)
                .ToArray();
            var visitStack = new List<ITypeSymbol> { type };
            foreach (var item in orderedCases)
                Visit(item.CaseType, visitStack, 1);
            if (orderedCases.Any(item => _failed.Contains(GetTypeName(item.CaseType))))
            {
                _failed.Add(typeName);
                return null;
            }

            var members = orderedCases
                .Select(item => new GeneratedMemberModel(
                    "__case_" + item.Discriminator.ToString(InvariantCulture),
                    "__case_" + item.Discriminator.ToString(InvariantCulture),
                    GetTypeName(item.CaseType),
                    checked((uint)item.Discriminator),
                    GeneratedMemberKind.Complex,
                    null,
                    0,
                    Required: false,
                    Nullable: item.CaseType.IsReferenceType,
                    NonNullableReference: false,
                    ConstructorBound: false,
                    InitializerBound: false,
                    HasExplicitId: true,
                    EnumUnderlyingType: null,
                    item.Location))
                .ToImmutableArray();
            var schema = new StringBuilder(typeName).Append('|').Append(UnionWireSemantic);
            foreach (var item in orderedCases)
            {
                schema.Append('|').Append(item.Discriminator).Append(':').Append(GetTypeName(item.CaseType));
            }
            var dependencyTypes = new List<ITypeSymbol>(orderedCases.Length + 1) { type };
            dependencyTypes.AddRange(orderedCases.Select(static item => (ITypeSymbol)item.CaseType));
            _models[typeName] = new GeneratedCodecModel(
                typeName,
                GetCodecName(typeName, _contractMode),
                GetSchemaId(typeName, schema.ToString()),
                GeneratedCodecKind.Union,
                type.IsReferenceType,
                members,
                ImmutableArray<string>.Empty,
                null,
                null,
                null,
                null,
                null,
                null,
                UnionWireSemantic,
                GetAssemblyDependencies(dependencyTypes),
                unionType.Locations.FirstOrDefault());

            var finalCases = ImmutableArray.CreateBuilder<FinalUnionCasePlan>(orderedCases.Length);
            foreach (var item in orderedCases)
            {
                var child = ResolveNativeUnionCaseCodecPlan(unionType, item, plans, resolving);
                if (child is null)
                {
                    _failed.Add(typeName);
                    return null;
                }
                finalCases.Add(new FinalUnionCasePlan(
                    item.Discriminator,
                    GetUnionCaseLogicalIdentity(item.CaseType),
                    child.TypeName));
            }
            return new FinalUnionCodecPlan(typeName, UnionWireSemantic, finalCases.ToImmutable());
        }

        private FinalCodecPlan? ResolveNativeUnionCaseCodecPlan(
            INamedTypeSymbol unionType,
            NativeUnionCase item,
            Dictionary<string, FinalCodecPlan> plans,
            HashSet<string> resolving)
        {
            var resolvingSnapshot = new HashSet<string>(resolving, StringComparer.Ordinal);
            try
            {
                return ResolveFinalCodecPlan(item.CaseType, plans, resolving);
            }
            catch (InvalidOperationException exception) when (
                exception.Message.StartsWith(
                    "Final Codec graph contains an unresolved recursive Codec selection at '",
                    StringComparison.Ordinal))
            {
                resolving.RemoveWhere(candidate => !resolvingSnapshot.Contains(candidate));
                Report(DtoDiagnosticKind.Unsupported, unionType,
                    $"native union case '{GetTypeName(item.CaseType)}' introduces a recursive final Codec dependency; native union case Codec dependencies must be acyclic",
                    item.Location);
                return null;
            }
        }

        private static RpcHashValue GetUnionCaseLogicalIdentity(ITypeSymbol caseType)
        {
            var parts = new List<string> { "union-case/v1" };
            AppendClosedTargetLogicalIdentity(caseType, parts);
            return Hashing.GetSemanticHash(parts.ToArray());
        }

        private readonly record struct NativeUnionCase(
            int Discriminator,
            INamedTypeSymbol CaseType,
            Location? Location);
    }
}
