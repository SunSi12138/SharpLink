namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static IEnumerable<string> GetCodecDependencies(GeneratedCodecModel codec)
    {
        if (codec.ElementType is not null)
            yield return codec.ElementType;
        if (codec.KeyType is not null)
            yield return codec.KeyType;
        if (codec.ValueType is not null)
            yield return codec.ValueType;
        foreach (var unionCase in codec.UnionCases)
            yield return unionCase.TypeName;
        foreach (var member in codec.Members)
        {
            if (member.Kind == GeneratedMemberKind.Complex)
                yield return member.TypeName;
        }
    }

    private static bool HasSameCodecDefinition(GeneratedCodecModel left, GeneratedCodecModel right)
    {
        if (!string.Equals(left.TypeName, right.TypeName, StringComparison.Ordinal) ||
            left.Kind != right.Kind || left.IsReferenceType != right.IsReferenceType ||
            !string.Equals(left.ElementType, right.ElementType, StringComparison.Ordinal) ||
            !string.Equals(left.KeyType, right.KeyType, StringComparison.Ordinal) ||
            !string.Equals(left.ValueType, right.ValueType, StringComparison.Ordinal) ||
            !string.Equals(left.CustomCodecType, right.CustomCodecType, StringComparison.Ordinal) ||
            !string.Equals(left.AdapterType, right.AdapterType, StringComparison.Ordinal) ||
            !string.Equals(left.AdapterId, right.AdapterId, StringComparison.Ordinal) ||
            !left.ConstructorMembers.SequenceEqual(right.ConstructorMembers, StringComparer.Ordinal) ||
            !left.AssemblyDependencies.SequenceEqual(right.AssemblyDependencies, StringComparer.Ordinal) ||
            !left.UnionCases.SequenceEqual(right.UnionCases) ||
            left.Members.Length != right.Members.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Members.Length; index++)
        {
            if (left.Members[index] with { Location = null } != right.Members[index] with { Location = null })
                return false;
        }
        return true;
    }

    private sealed record DtoAnalysisPassResult(
        ImmutableArray<GeneratedCodecModel> Codecs,
        ImmutableArray<DtoDiagnosticModel> Diagnostics,
        ImmutableArray<GeneratedEnumModel> Enums);

    private sealed partial class DtoAnalysisState
    {
        private const int MaximumDepth = 64;
        private readonly Compilation _compilation;
        private readonly CancellationToken _cancellationToken;
        private readonly bool _contractMode;
        private readonly bool _applyCodecPolicy;
        private readonly HashSet<string> _allowedAssemblyNames;
        private readonly Dictionary<ITypeSymbol, AdapterRegistration> _adaptersByType =
            new(SymbolEqualityComparer.Default);
        private readonly Dictionary<ITypeSymbol, AdapterRegistration> _adaptersBySelector =
            new(SymbolEqualityComparer.Default);
        private readonly Dictionary<ITypeSymbol, ExplicitBindingCandidate> _assemblyBindings =
            new(SymbolEqualityComparer.Default);
        private readonly Dictionary<ITypeSymbol, CustomCodecRegistration> _customCodecBindings =
            new(SymbolEqualityComparer.Default);
        private readonly Dictionary<string, GeneratedCodecModel> _models = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GeneratedEnumModel> _enums = new(StringComparer.Ordinal);
        private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
        private readonly HashSet<string> _diagnosticKeys = new(StringComparer.Ordinal);
        private readonly List<DtoDiagnosticModel> _diagnostics = [];

        public DtoAnalysisState(
            Compilation compilation,
            CancellationToken cancellationToken,
            bool contractMode,
            bool applyCodecPolicy)
            : this(
                compilation,
                cancellationToken,
                contractMode,
                applyCodecPolicy,
                selectorOnlyContractDefault: false)
        {
        }

        public DtoAnalysisPassResult Analyze()
        {
            ValidateDeclaredUnionRuntimeMappings();
            return AnalyzeDtoCandidates();
        }

        private void Report(
            DtoDiagnosticKind kind,
            ITypeSymbol type,
            string detail,
            Location? location = null)
        {
            var typeName = GetTypeName(type);
            var key = $"{kind}|{typeName}|{detail}";
            if (!_diagnosticKeys.Add(key))
                return;
            _diagnostics.Add(new DtoDiagnosticModel(
                kind,
                typeName,
                detail,
                location ?? type.Locations.FirstOrDefault()));
        }

        private void Report(
            DtoDiagnosticKind kind,
            IAssemblySymbol assembly,
            string detail,
            Location? location = null)
        {
            var key = $"{kind}|{assembly.Identity}|{detail}";
            if (!_diagnosticKeys.Add(key))
                return;
            _diagnostics.Add(new DtoDiagnosticModel(
                kind,
                assembly.Identity.ToString(),
                detail,
                location));
        }

        private static bool HasAttribute(ISymbol symbol, string ns, string name)
            => symbol.GetAttributes().Any(attribute => IsAttribute(attribute, ns, name));

        private static string EscapeIdentifier(string identifier)
            => Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(identifier) !=
               Microsoft.CodeAnalysis.CSharp.SyntaxKind.None
                ? "@" + identifier
                : identifier;

        private static string GetCodecName(string typeName, bool contractMode)
            => "__SharpLinkGeneratedCodec_" + ComputeHash((contractMode ? "contract|" : "standalone|") + typeName).ToString("X16", InvariantCulture);

        private static string GetSchemaId(string typeName, string schema)
            => typeName + ":" + ComputeHash(schema).ToString("X16", InvariantCulture);

        private static ulong ComputeHash(string value)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            foreach (var character in value)
            {
                hash ^= character;
                hash *= prime;
            }
            return hash;
        }
    }
}