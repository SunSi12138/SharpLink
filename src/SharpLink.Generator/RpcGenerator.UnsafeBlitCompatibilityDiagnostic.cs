namespace SharpLink.Generator;

/// <summary>
/// Reports non-blocking guidance for RPC payloads that ultimately use implicit UnsafeBlit over
/// source-defined AutoLayout value types.
/// </summary>
[Generator]
public sealed class UnsafeBlitCompatibilityDiagnosticGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var diagnostics = context.CompilationProvider.Select(static (compilation, cancellationToken) =>
            RpcGenerator.AnalyzeUnsafeBlitAutoLayoutDiagnostics(compilation, cancellationToken));

        context.RegisterSourceOutput(diagnostics, static (productionContext, items) =>
        {
            foreach (var diagnostic in items)
                productionContext.ReportDiagnostic(diagnostic);
        });
    }
}

public partial class RpcGenerator
{
    private const int AutoLayoutKindValue = 3;

    private static readonly DiagnosticDescriptor ImplicitUnsafeBlitAutoLayoutRule = new(
        id: "SHARPLINK064",
        title: "Implicit UnsafeBlit Contains Source-Defined AutoLayout",
        messageFormat: "RPC payload '{0}' uses implicit UnsafeBlit, and its recursive unmanaged field graph contains source-defined AutoLayout type '{1}' at '{2}'. Raw-memory wire layout can vary across runtimes; for stable cross-runtime raw wire prefer LayoutKind.Sequential or LayoutKind.Explicit, or bind an explicit custom/adapter codec.",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Source-defined AutoLayout inside an implicit UnsafeBlit payload can make raw-memory wire layout runtime-dependent. This diagnostic is advisory and does not change Codec selection or generated wire behavior.");

    internal static ImmutableArray<Diagnostic> AnalyzeUnsafeBlitAutoLayoutDiagnostics(
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        var codecAnalysis = AnalyzeGeneratedCodecsWithPolicyOwnership(compilation, cancellationToken);
        var finalCodecBoundTypes = new HashSet<string>(
            codecAnalysis.FinalCodecBoundTypes,
            StringComparer.Ordinal);
        var payloadRoots = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
        CollectCurrentContractPayloadRoots(compilation.Assembly.GlobalNamespace, payloadRoots);

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach (var pair in payloadRoots.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = pair.Value;
            if (!payload.IsUnmanagedType || finalCodecBoundTypes.Contains(pair.Key))
                continue;

            foreach (var hazard in FindSourceAutoLayoutHazards(payload, compilation.Assembly, cancellationToken))
            {
                diagnostics.Add(Diagnostic.Create(
                    ImplicitUnsafeBlitAutoLayoutRule,
                    hazard.Location,
                    pair.Key,
                    hazard.TypeName,
                    hazard.FieldPath));
            }
        }

        return diagnostics.ToImmutable();
    }

    private static void CollectCurrentContractPayloadRoots(
        INamespaceSymbol namespaceSymbol,
        Dictionary<string, ITypeSymbol> roots)
    {
        foreach (var type in namespaceSymbol.GetTypeMembers())
            CollectCurrentContractPayloadRoots(type, roots);
        foreach (var nestedNamespace in namespaceSymbol.GetNamespaceMembers())
            CollectCurrentContractPayloadRoots(nestedNamespace, roots);
    }

    private static void CollectCurrentContractPayloadRoots(
        INamedTypeSymbol type,
        Dictionary<string, ITypeSymbol> roots)
    {
        if (type.TypeKind == TypeKind.Interface && HasRpcContractAttribute(type))
        {
            foreach (var method in GetContractMethods(type))
            {
                foreach (var parameter in method.Parameters)
                {
                    if (IsCancellationTokenParameter(parameter))
                        continue;
                    if (IsAsyncEnumerable(parameter.Type, out var streamItem))
                        AddUnsafeBlitPayloadRoot(roots, streamItem!);
                    else
                        AddUnsafeBlitPayloadRoot(roots, parameter.Type);
                }

                if (IsAsyncEnumerable(method.ReturnType, out var returnStreamItem))
                {
                    AddUnsafeBlitPayloadRoot(roots, returnStreamItem!);
                }
                else if (method.ReturnType is INamedTypeSymbol { IsGenericType: true } taskLike &&
                         taskLike.TypeArguments.Length == 1)
                {
                    AddUnsafeBlitPayloadRoot(roots, taskLike.TypeArguments[0]);
                }
            }
        }

        foreach (var nested in type.GetTypeMembers())
            CollectCurrentContractPayloadRoots(nested, roots);
    }

    private static void AddUnsafeBlitPayloadRoot(
        Dictionary<string, ITypeSymbol> roots,
        ITypeSymbol type)
    {
        var typeName = GetTypeName(type);
        if (!roots.ContainsKey(typeName))
            roots.Add(typeName, type);
    }

    private static ImmutableArray<UnsafeBlitAutoLayoutHazard> FindSourceAutoLayoutHazards(
        ITypeSymbol root,
        IAssemblySymbol sourceAssembly,
        CancellationToken cancellationToken)
    {
        var hazards = ImmutableArray.CreateBuilder<UnsafeBlitAutoLayoutHazard>();
        var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        Visit(root, GetTypeName(root));
        return hazards
            .OrderBy(static item => item.TypeName, StringComparer.Ordinal)
            .ThenBy(static item => item.FieldPath, StringComparer.Ordinal)
            .ToImmutableArray();

        void Visit(ITypeSymbol type, string fieldPath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!type.IsUnmanagedType || !visited.Add(type) || type is not INamedTypeSymbol named)
                return;

            if (SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, sourceAssembly) &&
                HasExplicitAutoLayout(named))
            {
                var location = named.Locations.FirstOrDefault(static item => item.IsInSource)
                    ?? root.Locations.FirstOrDefault(static item => item.IsInSource)
                    ?? Location.None;
                hazards.Add(new UnsafeBlitAutoLayoutHazard(
                    GetTypeName(named),
                    fieldPath,
                    location));
            }

            foreach (var field in named.GetMembers().OfType<IFieldSymbol>()
                         .Where(static field => !field.IsStatic && !field.IsConst)
                         .OrderBy(static field => field.Name, StringComparer.Ordinal))
            {
                if (field.Type.IsUnmanagedType)
                    Visit(field.Type, fieldPath + "." + field.Name);
            }
        }
    }

    private static bool HasExplicitAutoLayout(INamedTypeSymbol type)
    {
        foreach (var attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass is not { Name: "StructLayoutAttribute" } attributeClass ||
                !string.Equals(
                    attributeClass.ContainingNamespace.ToDisplayString(),
                    "System.Runtime.InteropServices",
                    StringComparison.Ordinal) ||
                attribute.ConstructorArguments.Length == 0)
            {
                continue;
            }

            if (attribute.ConstructorArguments[0].Value is int layoutKind &&
                layoutKind == AutoLayoutKindValue)
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct UnsafeBlitAutoLayoutHazard(
        string TypeName,
        string FieldPath,
        Location Location);
}
