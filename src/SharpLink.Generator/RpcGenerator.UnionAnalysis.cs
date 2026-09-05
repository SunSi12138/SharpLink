namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private const string NativeUnionWireSemantic =
        "union/discriminator-varuint32/zero-null/remainder-case-payload/v1";

    private sealed partial class DtoAnalysisState
    {
        private bool TryVisitNativeUnion(ITypeSymbol type, List<ITypeSymbol> stack, int depth)
        {
            if (!TryGetNativeUnionCases(type, reportDiagnostics: true, out var cases))
                return false;

            var typeName = GetTypeName(type);
            if (cases.IsDefaultOrEmpty)
            {
                _failed.Add(typeName);
                return true;
            }

            stack.Add(type);
            foreach (var unionCase in cases)
                Visit(unionCase.Type, stack, depth + 1);
            stack.RemoveAt(stack.Count - 1);

            if (cases.Any(unionCase => _failed.Contains(GetTypeName(unionCase.Type))))
            {
                _failed.Add(typeName);
                return true;
            }

            var schema = new StringBuilder(NativeUnionWireSemantic);
            foreach (var unionCase in cases)
            {
                schema.Append('|')
                    .Append(unionCase.Discriminator.ToString(InvariantCulture))
                    .Append(':')
                    .Append(GetTypeName(unionCase.Type));
            }

            _models[typeName] = new GeneratedCodecModel(
                typeName,
                GetCodecName(typeName, _contractMode),
                GetSchemaId(typeName, schema.ToString()),
                GeneratedCodecKind.Union,
                IsReferenceType: true,
                ImmutableArray<GeneratedMemberModel>.Empty,
                ImmutableArray<string>.Empty,
                null,
                null,
                null,
                null,
                null,
                null,
                NativeUnionWireSemantic,
                GetAssemblyDependencies(cases.Select(static unionCase => unionCase.Type).Prepend(type)),
                type.Locations.FirstOrDefault())
            {
                UnionCases = cases
                    .Select(static unionCase => new GeneratedUnionCaseModel(
                        unionCase.Discriminator,
                        GetTypeName(unionCase.Type),
                        unionCase.Type.IsReferenceType))
                    .ToImmutableArray()
            };
            return true;
        }

        private bool TryGetNativeUnionCases(
            ITypeSymbol type,
            bool reportDiagnostics,
            out ImmutableArray<NativeUnionCaseSymbol> cases)
        {
            cases = ImmutableArray<NativeUnionCaseSymbol>.Empty;
            if (type is not INamedTypeSymbol unionType)
                return false;

            var attributes = unionType.GetAttributes()
                .Where(static attribute => IsAttribute(attribute, "SharpLink.Sdk", "RpcUnionCaseAttribute"))
                .ToImmutableArray();
            if (attributes.IsDefaultOrEmpty)
                return false;

            var builder = ImmutableArray.CreateBuilder<NativeUnionCaseSymbol>(attributes.Length);
            var valid = true;
            foreach (var attribute in attributes)
            {
                var location = attribute.ApplicationSyntaxReference?.GetSyntax(_cancellationToken).GetLocation() ??
                    unionType.Locations.FirstOrDefault();
                if (attribute.ConstructorArguments.Length != 2 ||
                    attribute.ConstructorArguments[0].Value is not int discriminator ||
                    attribute.ConstructorArguments[1].Value is not ITypeSymbol caseType)
                {
                    valid = false;
                    ReportInvalid("RpcUnionCase requires a positive discriminator and a concrete closed case type", location);
                    continue;
                }

                var invalidDetail = GetInvalidUnionCaseDetail(unionType, discriminator, caseType, _compilation);
                if (invalidDetail is not null)
                {
                    valid = false;
                    ReportInvalid(invalidDetail, location);
                    continue;
                }
                if (caseType is not INamedTypeSymbol namedCase || !IsAccessibleFromGeneratedCode(namedCase))
                {
                    valid = false;
                    ReportInvalid(
                        $"case type '{GetTypeName(caseType)}' and every containing type must be accessible from generated code",
                        location);
                    continue;
                }
                if (SymbolEqualityComparer.Default.Equals(unionType, namedCase))
                {
                    valid = false;
                    ReportInvalid("a union cannot register itself as one of its runtime cases", location);
                    continue;
                }

                builder.Add(new NativeUnionCaseSymbol(discriminator, namedCase, location));
            }

            if (!valid)
                return true;

            var ordered = builder
                .OrderBy(static item => item.Discriminator)
                .ThenBy(static item => GetTypeName(item.Type), StringComparer.Ordinal)
                .ToImmutableArray();
            var seenTags = new HashSet<int>();
            var seenTypes = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var item in ordered)
            {
                if (!seenTags.Add(item.Discriminator))
                {
                    valid = false;
                    ReportInvalid($"union discriminator {item.Discriminator} is declared more than once", item.Location);
                }
                if (!seenTypes.Add(item.Type))
                {
                    valid = false;
                    ReportInvalid($"case type '{GetTypeName(item.Type)}' is registered by more than one discriminator", item.Location);
                }
            }

            for (var leftIndex = 0; leftIndex < ordered.Length; leftIndex++)
            {
                for (var rightIndex = leftIndex + 1; rightIndex < ordered.Length; rightIndex++)
                {
                    var left = ordered[leftIndex];
                    var right = ordered[rightIndex];
                    if (!HasAmbiguousRuntimeUnionMapping(left.Type, right.Type))
                        continue;

                    valid = false;
                    ReportInvalid(
                        $"case types '{GetTypeName(left.Type)}' and '{GetTypeName(right.Type)}' overlap by inheritance, so one runtime value could match multiple union discriminators",
                        right.Location ?? left.Location);
                }
            }

            cases = ordered;
            return true;

            void ReportInvalid(string detail, Location? location)
            {
                if (!reportDiagnostics)
                    return;
                Report(DtoDiagnosticKind.Unsupported, unionType, detail, location);
            }
        }

        private bool HasAmbiguousRuntimeUnionMapping(ITypeSymbol left, ITypeSymbol right)
        {
            if (_compilation is not Microsoft.CodeAnalysis.CSharp.CSharpCompilation csharpCompilation)
                return false;
            return csharpCompilation.ClassifyConversion(left, right).IsImplicit ||
                   csharpCompilation.ClassifyConversion(right, left).IsImplicit;
        }

        private sealed record NativeUnionCaseSymbol(
            int Discriminator,
            INamedTypeSymbol Type,
            Location? Location);
    }
}