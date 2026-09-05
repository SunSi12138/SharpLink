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
        var standalone = standaloneState.AnalyzeSharpPackBindings(standaloneGraph);

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
        var contract = contractState.AnalyzeSharpPackBindings(contractGraph);

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

    private static bool IsSharpPackAdapter(GeneratedCodecModel model)
        => model.Kind == GeneratedCodecKind.Adapter &&
           string.Equals(model.AdapterId, SharpPackAdapterId, StringComparison.Ordinal) &&
           string.Equals(model.AdapterType, SharpPackAdapterTypeName, StringComparison.Ordinal);

    private sealed partial class DtoAnalysisState
    {
        internal SharpPackIntegrationAnalysisResult AnalyzeSharpPackBindings(FinalCodecGraph graph)
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

            var analysis = new SharpPackSidecarAnalysis(_compilation);
            foreach (var root in rootPlans)
            {
                if (!TryResolveReachableType(root.TypeName, out var rootType))
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
    }

    private sealed class SharpPackSidecarAnalysis
    {
        private readonly Compilation _compilation;
        private readonly INamedTypeSymbol? _sharpPackable;
        private readonly INamedTypeSymbol? _formatterFactory;
        private readonly INamedTypeSymbol? _contextFormatterFactory;
        private readonly Dictionary<string, int> _states = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SharpPackSidecarModel> _sidecars = new(StringComparer.Ordinal);
        private readonly List<SharpPackIntegrationDiagnosticModel> _diagnostics = [];
        private readonly HashSet<string> _diagnosticKeys = new(StringComparer.Ordinal);

        internal SharpPackSidecarAnalysis(Compilation compilation)
        {
            _compilation = compilation;
            _sharpPackable = compilation.GetTypeByMetadataName("SharpPack.ISharpPackable`1");
            _formatterFactory = compilation.GetTypeByMetadataName("SharpPack.ISharpPackFormatterFactory`1");
            _contextFormatterFactory = compilation.GetTypeByMetadataName(
                "SharpPack.ISharpPackContextFormatterFactory`1");
        }

        internal void AnalyzeRoot(ITypeSymbol rootType)
            => _ = AnalyzeType(rootType, GetTypeName(rootType), rootType.Locations.FirstOrDefault());

        internal void Report(string typeName, string detail, Location? location)
        {
            var key = typeName + "|" + detail;
            if (!_diagnosticKeys.Add(key))
                return;
            _diagnostics.Add(new SharpPackIntegrationDiagnosticModel(typeName, detail, location));
        }

        internal SharpPackIntegrationAnalysisResult ToResult()
            => new(
                _sidecars.Values
                    .OrderBy(static item => item.TypeName, StringComparer.Ordinal)
                    .ToImmutableArray(),
                _diagnostics.ToImmutableArray(),
                HasBindings: true);

        private bool AnalyzeType(ITypeSymbol type, string path, Location? location)
        {
            var typeName = GetTypeName(type);
            if (_states.TryGetValue(typeName, out var existingState))
                return existingState != 3;

            if (type is ITypeParameterSymbol || ContainsTypeParameter(type))
                return Fail(typeName, path, "the payload path contains an unresolved generic type parameter", location);
            if (type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer || type.IsRefLikeType)
                return Fail(typeName, path, "pointer, function-pointer, and ref-like shapes are not supported", location);

            if (type is IArrayTypeSymbol array)
            {
                if (array.Rank is < 1 or > 4)
                    return Fail(typeName, path, $"SharpPack does not provide an array formatter for rank {array.Rank}", location);
                _states[typeName] = 1;
                var ok = AnalyzeType(array.ElementType, path + " -> element", location);
                _states[typeName] = ok ? 2 : 3;
                return ok;
            }

            if (type is not INamedTypeSymbol named)
                return Fail(typeName, path, "the CLR type shape is not a closed named type", location);

            if (named.TypeKind == TypeKind.Enum || named.IsUnmanagedType || HasExistingSharpPackSupport(named))
            {
                _states[typeName] = 2;
                return true;
            }

            if (IsSharpPackWellKnownManagedType(named))
            {
                _states[typeName] = 2;
                return true;
            }

            if (IsSharpPackKnownGenericType(named))
            {
                _states[typeName] = 1;
                var ok = true;
                for (var index = 0; index < named.TypeArguments.Length; index++)
                {
                    ok &= AnalyzeType(
                        named.TypeArguments[index],
                        path + $" -> type argument {index + 1}",
                        location);
                }
                _states[typeName] = ok ? 2 : 3;
                return ok;
            }

            if (named.SpecialType == SpecialType.System_Object ||
                named.TypeKind is TypeKind.Interface or TypeKind.Delegate ||
                named.IsAbstract)
            {
                return Fail(
                    typeName,
                    path,
                    "the type is abstract/non-instantiable or requires runtime-polymorphic formatter selection",
                    location);
            }

            if (named.TypeKind is not (TypeKind.Class or TypeKind.Struct))
                return Fail(typeName, path, $"type kind '{named.TypeKind}' is not supported by sidecar generation", location);

            if (!_compilation.IsSymbolAccessibleWithin(named, _compilation.Assembly))
                return Fail(typeName, path, "the type itself is not accessible to generated Contract code", location);

            _states[typeName] = 1;
            if (!TryBuildSidecar(named, path, out var sidecar, out var failure, out var failureLocation))
            {
                _states[typeName] = 3;
                return Fail(typeName, path, failure, failureLocation ?? location);
            }

            var success = true;
            foreach (var member in sidecar.Members)
            {
                var symbol = FindMember(named, member.Name);
                var memberType = symbol is null ? null : GetMemberType(symbol);
                if (memberType is null)
                {
                    success = false;
                    Fail(typeName, path + " -> member '" + member.Name + "'", "member type metadata cannot be resolved", failureLocation ?? location);
                    continue;
                }
                success &= AnalyzeType(
                    memberType,
                    path + " -> member '" + member.Name + "'",
                    symbol.Locations.FirstOrDefault() ?? location);
            }

            if (!success)
            {
                _states[typeName] = 3;
                return false;
            }

            _sidecars[typeName] = sidecar;
            _states[typeName] = 2;
            return true;
        }

        private bool TryBuildSidecar(
            INamedTypeSymbol type,
            string path,
            out SharpPackSidecarModel sidecar,
            out string failure,
            out Location? failureLocation)
        {
            var members = new List<(ISymbol Symbol, ITypeSymbol Type, int Order)>();
            var memberByName = new Dictionary<string, int>(StringComparer.Ordinal);
            var hierarchy = new Stack<INamedTypeSymbol>();
            for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
                hierarchy.Push(current);

            var sequentialOrder = 0;
            while (hierarchy.Count != 0)
            {
                var current = hierarchy.Pop();
                foreach (var symbol in current.GetMembers())
                {
                    if (symbol.IsStatic || symbol.IsImplicitlyDeclared || !symbol.CanBeReferencedByName ||
                        symbol is not (IFieldSymbol or IPropertySymbol))
                    {
                        continue;
                    }

                    var include = HasAttribute(symbol, "SharpPack", "SharpPackIncludeAttribute");
                    if (HasAttribute(symbol, "SharpPack", "SharpPackIgnoreAttribute"))
                        continue;

                    if (symbol is IPropertySymbol property &&
                        (property.IsIndexer || property.GetMethod is null))
                    {
                        continue;
                    }

                    var publicForRead = symbol.DeclaredAccessibility == Accessibility.Public &&
                        (symbol is not IPropertySymbol readableProperty ||
                         readableProperty.GetMethod?.DeclaredAccessibility == Accessibility.Public);
                    if (!publicForRead)
                    {
                        if (include)
                        {
                            sidecar = null!;
                            failure = $"member '{symbol.Name}' is explicitly included by SharpPack but is inaccessible to a sidecar formatter";
                            failureLocation = symbol.Locations.FirstOrDefault();
                            return false;
                        }
                        continue;
                    }

                    if (HasSharpPackCustomFormatterAttribute(symbol))
                    {
                        sidecar = null!;
                        failure = $"member '{symbol.Name}' uses a member-level SharpPack custom formatter that cannot be reproduced by the typed sidecar path";
                        failureLocation = symbol.Locations.FirstOrDefault();
                        return false;
                    }

                    var memberType = GetMemberType(symbol);
                    if (memberType is null)
                        continue;
                    var order = TryGetSharpPackOrder(symbol, out var explicitOrder)
                        ? explicitOrder
                        : sequentialOrder;
                    sequentialOrder++;

                    if (memberByName.TryGetValue(symbol.Name, out var existingIndex))
                    {
                        members[existingIndex] = (symbol, memberType, order);
                    }
                    else
                    {
                        memberByName.Add(symbol.Name, members.Count);
                        members.Add((symbol, memberType, order));
                    }
                }
            }

            if (members.Count >= 250)
            {
                sidecar = null!;
                failure = $"the SharpPack object envelope supports at most 249 members, but this shape exposes {members.Count}";
                failureLocation = type.Locations.FirstOrDefault();
                return false;
            }

            var duplicateOrder = members
                .GroupBy(static item => item.Order)
                .FirstOrDefault(static group => group.Count() > 1 && group.Any(item =>
                    TryGetSharpPackOrder(item.Symbol, out _)));
            if (duplicateOrder is not null)
            {
                sidecar = null!;
                failure = $"members use an ambiguous duplicate SharpPack order value {duplicateOrder.Key}";
                failureLocation = duplicateOrder.First().Symbol.Locations.FirstOrDefault();
                return false;
            }

            members = members
                .OrderBy(static item => item.Order)
                .ThenBy(static item => item.Symbol.Name, StringComparer.Ordinal)
                .ToList();

            if (!TrySelectConstruction(type, members, out var constructorMembers, out failure, out failureLocation))
            {
                sidecar = null!;
                return false;
            }

            sidecar = new SharpPackSidecarModel(
                GetTypeName(type),
                "__SharpLinkSharpPackFormatter_" + Hashing.GetIdentifierHash(GetTypeName(type)),
                type.IsReferenceType,
                members.Select(static item => new SharpPackSidecarMemberModel(
                        item.Symbol.Name,
                        EscapeIdentifier(item.Symbol.Name),
                        GetTypeName(item.Type),
                        item.Order))
                    .ToImmutableArray(),
                constructorMembers);
            failure = string.Empty;
            failureLocation = null;
            return true;
        }

        private bool TrySelectConstruction(
            INamedTypeSymbol type,
            List<(ISymbol Symbol, ITypeSymbol Type, int Order)> members,
            out ImmutableArray<string> constructorMembers,
            out string failure,
            out Location? failureLocation)
        {
            if (type.IsValueType)
            {
                var blocked = members.FirstOrDefault(static item => !CanInitialize(item.Symbol));
                if (blocked.Symbol is not null)
                {
                    constructorMembers = ImmutableArray<string>.Empty;
                    failure = $"value-type member '{blocked.Symbol.Name}' is not assignable during generated deserialization";
                    failureLocation = blocked.Symbol.Locations.FirstOrDefault();
                    return false;
                }

                constructorMembers = ImmutableArray<string>.Empty;
                failure = string.Empty;
                failureLocation = null;
                return true;
            }

            var constructors = type.InstanceConstructors
                .Where(ctor => !ctor.IsStatic && _compilation.IsSymbolAccessibleWithin(ctor, _compilation.Assembly))
                .ToImmutableArray();
            var parameterless = constructors.FirstOrDefault(static ctor => ctor.Parameters.Length == 0);
            if (parameterless is not null && members.All(static item => CanInitialize(item.Symbol)))
            {
                constructorMembers = ImmutableArray<string>.Empty;
                failure = string.Empty;
                failureLocation = null;
                return true;
            }

            var annotated = constructors
                .Where(static ctor => HasAttribute(ctor, "SharpPack", "SharpPackConstructorAttribute"))
                .ToImmutableArray();
            var candidates = annotated.IsDefaultOrEmpty ? constructors : annotated;
            var matches = new List<(IMethodSymbol Constructor, ImmutableArray<string> Members)>();
            foreach (var constructor in candidates)
            {
                var matchedNames = ImmutableArray.CreateBuilder<string>(constructor.Parameters.Length);
                var matchedSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                var valid = true;
                foreach (var parameter in constructor.Parameters)
                {
                    var member = members.FirstOrDefault(item =>
                        string.Equals(item.Symbol.Name, parameter.Name, StringComparison.OrdinalIgnoreCase) &&
                        SymbolEqualityComparer.Default.Equals(item.Type, parameter.Type));
                    if (member.Symbol is null)
                    {
                        valid = false;
                        break;
                    }
                    matchedNames.Add(member.Symbol.Name);
                    matchedSymbols.Add(member.Symbol);
                }
                if (!valid || members.Any(item =>
                        !matchedSymbols.Contains(item.Symbol) && !CanInitialize(item.Symbol)))
                {
                    continue;
                }
                matches.Add((constructor, matchedNames.ToImmutable()));
            }

            if (matches.Count == 1)
            {
                constructorMembers = matches[0].Members;
                failure = string.Empty;
                failureLocation = null;
                return true;
            }

            constructorMembers = ImmutableArray<string>.Empty;
            failure = matches.Count == 0
                ? "no accessible constructor/member-assignment plan can recreate the serialized public member set"
                : "multiple accessible constructors match the serialized public member set; select an authoritative SharpPack formatter or DTO wrapper";
            failureLocation = type.Locations.FirstOrDefault();
            return false;
        }

        private bool HasExistingSharpPackSupport(INamedTypeSymbol type)
        {
            if (HasAttribute(type, "SharpPack", "SharpPackableAttribute") ||
                HasAttribute(type, "SharpPack", "SharpPackUnionAttribute"))
            {
                return true;
            }

            foreach (var iface in type.AllInterfaces)
            {
                if ((_sharpPackable is not null &&
                     SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, _sharpPackable)) ||
                    (_formatterFactory is not null &&
                     SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, _formatterFactory)) ||
                    (_contextFormatterFactory is not null &&
                     SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, _contextFormatterFactory)))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsSharpPackWellKnownManagedType(INamedTypeSymbol type)
            => SharpPackWellKnownManagedTypes.Contains(GetMetadataName(type));

        private static bool IsSharpPackKnownGenericType(INamedTypeSymbol type)
            => type.IsGenericType &&
               SharpPackKnownGenericTypes.Contains(GetMetadataName(type.OriginalDefinition));

        private static string GetMetadataName(INamedTypeSymbol type)
        {
            var ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            return string.IsNullOrEmpty(ns) ? type.MetadataName : ns + "." + type.MetadataName;
        }

        private static bool ContainsTypeParameter(ITypeSymbol type)
        {
            if (type is ITypeParameterSymbol)
                return true;
            if (type is IArrayTypeSymbol array)
                return ContainsTypeParameter(array.ElementType);
            return type is INamedTypeSymbol named &&
                   named.TypeArguments.Any(ContainsTypeParameter);
        }

        private static bool CanInitialize(ISymbol symbol)
            => symbol switch
            {
                IFieldSymbol field => !field.IsReadOnly && field.DeclaredAccessibility == Accessibility.Public,
                IPropertySymbol property => property.SetMethod?.DeclaredAccessibility == Accessibility.Public,
                _ => false
            };

        private static ISymbol? FindMember(INamedTypeSymbol type, string memberName)
        {
            for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            {
                var member = current.GetMembers(memberName)
                    .FirstOrDefault(static item => item is IFieldSymbol or IPropertySymbol);
                if (member is not null)
                    return member;
            }
            return null;
        }

        private static bool TryGetSharpPackOrder(ISymbol symbol, out int order)
        {
            foreach (var attribute in symbol.GetAttributes())
            {
                if (!IsAttribute(attribute, "SharpPack", "SharpPackOrderAttribute") ||
                    attribute.ConstructorArguments.Length == 0 ||
                    attribute.ConstructorArguments[0].Value is not int value)
                {
                    continue;
                }
                order = value;
                return true;
            }
            order = 0;
            return false;
        }

        private static bool HasSharpPackCustomFormatterAttribute(ISymbol symbol)
        {
            foreach (var attribute in symbol.GetAttributes())
            {
                for (var current = attribute.AttributeClass; current is not null; current = current.BaseType)
                {
                    if (string.Equals(current.ContainingNamespace?.ToDisplayString(), "SharpPack", StringComparison.Ordinal) &&
                        string.Equals(current.MetadataName, "SharpPackCustomFormatterAttribute`1", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private bool Fail(string typeName, string path, string reason, Location? location)
        {
            _states[typeName] = 3;
            Report(
                typeName,
                $"{path}: {reason}. Use an explicit RpcCodec/RpcCodecAdapter, a supported DTO wrapper, or an authoritative SharpPack formatter.",
                location);
            return false;
        }
    }

    private static string GenerateSharpPackIntegration(SharpPackIntegrationAnalysisResult analysis)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS0618");
        sb.AppendLine("#pragma warning disable CS8600");
        sb.AppendLine("#pragma warning disable CS8601");
        sb.AppendLine("#pragma warning disable CS8602");
        sb.AppendLine("#pragma warning disable CS8604");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Buffers;");
        sb.AppendLine("using SharpLink.Abstractions;");
        sb.AppendLine();
        sb.AppendLine("namespace SharpLink.Generated;");
        sb.AppendLine();

        foreach (var sidecar in analysis.Sidecars)
            AppendSharpPackSidecar(sb, sidecar);

        sb.AppendLine("internal static class __SharpLinkGeneratedSharpPackIntegration");
        sb.AppendLine("{");
        sb.AppendLine("    internal static void Configure(IRpcCodecAdapterScope adapterScope)");
        sb.AppendLine("    {");
        if (analysis.Sidecars.IsDefaultOrEmpty)
        {
            sb.AppendLine("        _ = adapterScope;");
        }
        else
        {
            var identity = Hashing.GetIdentifierHash(string.Join(
                "|",
                analysis.Sidecars.Select(static item => item.TypeName)));
            sb.AppendLine("        if (adapterScope is not global::SharpLink.Serializer.SharpPack.ISharpPackRpcCodecAdapterScopeConfiguration configurable)");
            sb.AppendLine("            throw new InvalidOperationException(\"SharpPack adapter scope does not support generated Contract formatter configuration.\");");
            sb.AppendLine($"        configurable.Configure(\"slsp/{identity}\", static builder =>");
            sb.AppendLine("        {");
            foreach (var sidecar in analysis.Sidecars)
            {
                sb.AppendLine($"            builder.Register<{sidecar.TypeName}>(new {sidecar.FormatterName}());");
            }
            sb.AppendLine("        });");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void AppendSharpPackSidecar(StringBuilder sb, SharpPackSidecarModel sidecar)
    {
        var nullableSuffix = sidecar.IsReferenceType ? "?" : string.Empty;
        sb.AppendLine($"internal sealed class {sidecar.FormatterName} : global::SharpPack.SharpPackFormatter<{sidecar.TypeName}>");
        sb.AppendLine("{");
        sb.AppendLine("    public override void Serialize<TBufferWriter>(");
        sb.AppendLine("        ref global::SharpPack.SharpPackWriter<TBufferWriter> writer,");
        sb.AppendLine($"        scoped ref {sidecar.TypeName}{nullableSuffix} value)");
        sb.AppendLine("        where TBufferWriter : IBufferWriter<byte>");
        sb.AppendLine("    {");
        if (sidecar.IsReferenceType)
        {
            sb.AppendLine("        if (value is null)");
            sb.AppendLine("        {");
            sb.AppendLine("            writer.WriteNullObjectHeader();");
            sb.AppendLine("            return;");
            sb.AppendLine("        }");
        }
        sb.AppendLine($"        writer.WriteObjectHeader({sidecar.Members.Length});");
        foreach (var member in sidecar.Members)
            sb.AppendLine($"        writer.WriteValue<{member.TypeName}>(value{(sidecar.IsReferenceType ? "!" : string.Empty)}.{member.Identifier});");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public override void Deserialize(");
        sb.AppendLine("        ref global::SharpPack.SharpPackReader reader,");
        sb.AppendLine($"        scoped ref {sidecar.TypeName}{nullableSuffix} value)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (!reader.TryReadObjectHeader(out var count))");
        sb.AppendLine("        {");
        sb.AppendLine("            value = default!;");
        sb.AppendLine("            return;");
        sb.AppendLine("        }");
        sb.AppendLine($"        if (count > {sidecar.Members.Length})");
        sb.AppendLine($"            global::SharpPack.SharpPackSerializationException.ThrowInvalidPropertyCount(typeof({sidecar.TypeName}), {sidecar.Members.Length}, count);");
        sb.AppendLine();
        for (var index = 0; index < sidecar.Members.Length; index++)
        {
            var member = sidecar.Members[index];
            sb.AppendLine($"        {member.TypeName} __slsp_{index} = default!;");
            sb.AppendLine($"        if (count > {index})");
            sb.AppendLine($"            __slsp_{index} = reader.ReadValue<{member.TypeName}>();");
        }
        if (sidecar.Members.Length != 0)
            sb.AppendLine();

        var indexByName = sidecar.Members
            .Select((member, index) => (member.Name, Index: index))
            .ToDictionary(static item => item.Name, static item => item.Index, StringComparer.Ordinal);
        var constructorArguments = sidecar.ConstructorMembers
            .Select(name => "__slsp_" + indexByName[name])
            .ToArray();
        var constructorSet = new HashSet<string>(sidecar.ConstructorMembers, StringComparer.Ordinal);
        var initializerMembers = sidecar.Members
            .Where(member => !constructorSet.Contains(member.Name))
            .ToArray();

        sb.Append($"        value = new {sidecar.TypeName}({string.Join(", ", constructorArguments)})");
        if (initializerMembers.Length == 0)
        {
            sb.AppendLine(";");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("        {");
            foreach (var member in initializerMembers)
            {
                sb.AppendLine($"            {member.Identifier} = __slsp_{indexByName[member.Name]},");
            }
            sb.AppendLine("        };");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
    }
}
