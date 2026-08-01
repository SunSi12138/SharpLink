namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static ClusterRouteAnalysis AnalyzeClusterRoutes(Compilation compilation, CancellationToken cancellationToken)
    {
        var routes = ImmutableArray.CreateBuilder<ClusterRouteModel>();
        var diagnostics = ImmutableArray.CreateBuilder<ClusterRouteDiagnostic>();
        var owners = new Dictionary<IAssemblySymbol, ClusterRouteModel>(SymbolEqualityComparer.Default);

        foreach (var attribute in compilation.Assembly.GetAttributes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attribute.AttributeClass?.ToDisplayString() != ClusterContractAssemblyAttributeMetadataName)
                continue;

            var location = attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? Location.None;
            if (attribute.ConstructorArguments.Length != 2 ||
                attribute.ConstructorArguments[0].Value is not string cluster ||
                attribute.ConstructorArguments[1].Value is not INamedTypeSymbol marker)
            {
                diagnostics.Add(new ClusterRouteDiagnostic(InvalidClusterRouteAttributeRule, location, []));
                continue;
            }
            if (!IsValidClusterKey(cluster))
            {
                diagnostics.Add(new ClusterRouteDiagnostic(InvalidClusterKeyRule, location, [cluster]));
                continue;
            }
            if (!HasGeneratedManifest(marker.ContainingAssembly, compilation.Assembly))
            {
                diagnostics.Add(new ClusterRouteDiagnostic(
                    MissingClusterRouteManifestRule,
                    location,
                    [marker.ContainingAssembly.Identity.ToString()]));
                continue;
            }

            var route = new ClusterRouteModel(
                cluster,
                marker.ContainingAssembly.Identity.ToString(),
                marker.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            if (owners.TryGetValue(marker.ContainingAssembly, out var existing))
            {
                if (!string.Equals(existing.Cluster, route.Cluster, StringComparison.Ordinal))
                {
                    diagnostics.Add(new ClusterRouteDiagnostic(
                        ConflictingClusterRouteRule,
                        location,
                        [route.AssemblyIdentity, existing.Cluster, route.Cluster]));
                }
                continue;
            }

            owners.Add(marker.ContainingAssembly, route);
            routes.Add(route);
        }

        return new ClusterRouteAnalysis(
            routes.OrderBy(static route => route.Cluster, StringComparer.Ordinal)
                .ThenBy(static route => route.AssemblyIdentity, StringComparer.Ordinal)
                .ToImmutableArray(),
            diagnostics.ToImmutable());
    }

    private static bool HasGeneratedManifest(IAssemblySymbol assembly, IAssemblySymbol currentAssembly)
    {
        if (assembly.GetAttributes().Any(static attribute =>
            attribute.AttributeClass?.ToDisplayString() ==
                "SharpLink.Abstractions.SharpLinkGeneratedAssemblyManifestAttribute"))
        {
            return true;
        }

        return SymbolEqualityComparer.Default.Equals(assembly, currentAssembly) &&
            AssemblyContainsRpcContract(assembly.GlobalNamespace);
    }

    private static bool AssemblyContainsRpcContract(INamespaceSymbol @namespace)
    {
        foreach (var type in @namespace.GetTypeMembers())
        {
            if (ContainsRpcContract(type))
                return true;
        }
        foreach (var child in @namespace.GetNamespaceMembers())
        {
            if (AssemblyContainsRpcContract(child))
                return true;
        }
        return false;
    }

    private static bool ContainsRpcContract(INamedTypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Interface && HasRpcContractAttribute(type))
            return true;
        return type.GetTypeMembers().Any(ContainsRpcContract);
    }

    // Kept byte-for-byte equivalent to SharpLinkClusterKey.IsValid so generator diagnostics match runtime validation.
    private static bool IsValidClusterKey(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64 || !IsAsciiAlphaNumeric(value[0]))
            return false;
        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (!IsAsciiAlphaNumeric(character) && character is not '.' and not '_' and not '-')
                return false;
        }
        return true;
    }

    private static bool IsAsciiAlphaNumeric(char value)
        => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private readonly record struct ClusterRouteModel(
        string Cluster,
        string AssemblyIdentity,
        string MarkerTypeName);

    private readonly record struct ClusterRouteDiagnostic(
        DiagnosticDescriptor Rule,
        Location Location,
        object[] Arguments);

    private readonly record struct ClusterRouteAnalysis(
        ImmutableArray<ClusterRouteModel> Routes,
        ImmutableArray<ClusterRouteDiagnostic> Diagnostics);
}
