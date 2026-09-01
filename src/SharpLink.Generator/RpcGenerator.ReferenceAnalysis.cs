namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static ImmutableArray<RpcInterfaceModel> GetReferencedInterfaceModels(
        Compilation compilation,
        CancellationToken _)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var models = ImmutableArray.CreateBuilder<RpcInterfaceModel>();
        var candidateAssemblyNames = ResolveReferenceAssemblyNames(compilation);

        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
                continue;

            if (!candidateAssemblyNames.Contains(assembly.Identity.Name))
                continue;

            CollectReferencedInterfaces(assembly.GlobalNamespace, models, seen);
        }

        return models
            .OrderBy(static m => m.FullName, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static ImmutableArray<RpcServiceModel> GetReferencedServiceModels(
        Compilation compilation,
        CancellationToken _)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var models = ImmutableArray.CreateBuilder<RpcServiceModel>();
        var candidateAssemblyNames = ResolveReferenceAssemblyNames(compilation);

        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
                continue;

            if (!candidateAssemblyNames.Contains(assembly.Identity.Name))
                continue;

            CollectReferencedServices(assembly.GlobalNamespace, models, seen);
        }

        return models
            .OrderBy(static m => m.ServiceFullName, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static ImmutableArray<StaticRouteConflictModel> AnalyzeStaticRouteConflicts(
        Compilation compilation,
        CancellationToken _)
    {
        var contracts = new List<(RpcInterfaceModel Model, string Owner, Location? Location)>();
        var services = new List<(RpcServiceModel Model, string Owner, Location? Location)>();
        var candidateAssemblyNames = ResolveReferenceAssemblyNames(compilation);

        CollectStaticRouteModels(compilation.Assembly, contracts, services);
        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly &&
                candidateAssemblyNames.Contains(assembly.Identity.Name))
            {
                CollectStaticRouteModels(assembly, contracts, services);
            }
        }

        var conflicts = ImmutableArray.CreateBuilder<StaticRouteConflictModel>();
        foreach (var group in contracts.GroupBy(static contract => contract.Model.Hash))
        {
            var ordered = group
                .OrderBy(static contract => contract.Owner, StringComparer.Ordinal)
                .ThenBy(static contract => contract.Model.FullName, StringComparer.Ordinal)
                .ToArray();
            if (ordered.Length < 2)
                continue;

            var first = ordered[0];
            for (var index = 1; index < ordered.Length; index++)
            {
                var incoming = ordered[index];
                if (!string.Equals(first.Owner, incoming.Owner, StringComparison.Ordinal))
                {
                    conflicts.Add(new StaticRouteConflictModel(
                        StaticRouteConflictKind.Contract,
                        incoming.Model.FullName,
                        incoming.Model.Hash,
                        $"{first.Owner}:{first.Model.Fingerprint}",
                        $"{incoming.Owner}:{incoming.Model.Fingerprint}",
                        incoming.Location));
                }

                foreach (var firstMethod in first.Model.Methods)
                {
                    var incomingMethod = incoming.Model.Methods.FirstOrDefault(method => method.Hash == firstMethod.Hash);
                    if (incomingMethod is null ||
                        string.Equals(firstMethod.Fingerprint, incomingMethod.Fingerprint, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    conflicts.Add(new StaticRouteConflictModel(
                        StaticRouteConflictKind.Method,
                        $"{incoming.Model.FullName}.{incomingMethod.Name}",
                        incomingMethod.Hash,
                        firstMethod.Fingerprint,
                        incomingMethod.Fingerprint,
                        incoming.Location));
                }
            }
        }

        foreach (var group in services.GroupBy(static service => service.Model.Interface.Hash))
        {
            var ordered = group
                .OrderBy(static service => service.Owner, StringComparer.Ordinal)
                .ThenBy(static service => service.Model.ServiceFullName, StringComparer.Ordinal)
                .ToArray();
            if (ordered.Length < 2)
                continue;
            var first = ordered[0];
            for (var index = 1; index < ordered.Length; index++)
            {
                var incoming = ordered[index];
                conflicts.Add(new StaticRouteConflictModel(
                    StaticRouteConflictKind.Service,
                    incoming.Model.Interface.FullName,
                    incoming.Model.Interface.Hash,
                    first.Model.ServiceFullName,
                    incoming.Model.ServiceFullName,
                    incoming.Location));
            }
        }

        return conflicts
            .Distinct()
            .OrderBy(static conflict => conflict.Kind)
            .ThenBy(static conflict => conflict.Id)
            .ToImmutableArray();
    }

    private static void CollectStaticRouteModels(
        IAssemblySymbol assembly,
        List<(RpcInterfaceModel Model, string Owner, Location? Location)> contracts,
        List<(RpcServiceModel Model, string Owner, Location? Location)> services)
        => CollectStaticRouteModels(assembly.GlobalNamespace, assembly.Identity.ToString(), contracts, services);

    private static void CollectStaticRouteModels(
        INamespaceSymbol namespaceSymbol,
        string owner,
        List<(RpcInterfaceModel Model, string Owner, Location? Location)> contracts,
        List<(RpcServiceModel Model, string Owner, Location? Location)> services)
    {
        foreach (var type in namespaceSymbol.GetTypeMembers())
            CollectStaticRouteModels(type, owner, contracts, services);
        foreach (var child in namespaceSymbol.GetNamespaceMembers())
            CollectStaticRouteModels(child, owner, contracts, services);
    }

    private static void CollectStaticRouteModels(
        INamedTypeSymbol type,
        string owner,
        List<(RpcInterfaceModel Model, string Owner, Location? Location)> contracts,
        List<(RpcServiceModel Model, string Owner, Location? Location)> services)
    {
        if (type.TypeKind == TypeKind.Interface && HasRpcContractAttribute(type) &&
            InheritsIService(type) && !HasInvalidRpcMethod(type))
        {
            contracts.Add((CreateInterfaceModel(type), owner, type.Locations.FirstOrDefault()));
        }

        if (type.TypeKind == TypeKind.Class && !type.IsAbstract && !type.IsGenericType &&
            type.GetAttributes().Any(IsRpcServiceAttribute))
        {
            var rpcContracts = type.AllInterfaces.Where(HasRpcContractAttribute).ToArray();
            var constructor = SelectServiceConstructor(type);
            if (rpcContracts.Length == 1 && constructor is not null &&
                IsServiceConstructorSupported(constructor, out _) &&
                !HasInvalidRpcMethod(rpcContracts[0]))
            {
                var serviceNamespace = type.ContainingNamespace.IsGlobalNamespace
                    ? string.Empty
                    : type.ContainingNamespace.ToDisplayString();
                services.Add((new RpcServiceModel(
                    type.Name,
                    serviceNamespace,
                    type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    CreateInterfaceModel(rpcContracts[0]),
                    GetServiceLifetime(type, out _),
                    constructor.Parameters.Select(static parameter => new RpcConstructorParameterModel(
                        parameter.Name,
                        parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))).ToImmutableArray(),
                    ImmutableArray.Create(rpcContracts[0].ContainingAssembly.Identity.ToString()),
                    type.Locations.FirstOrDefault()),
                    owner,
                    type.Locations.FirstOrDefault()));
            }
        }

        foreach (var nested in type.GetTypeMembers())
            CollectStaticRouteModels(nested, owner, contracts, services);
    }

    private static HashSet<string> ResolveReferenceAssemblyNames(Compilation compilation)
    {
        var explicitAssemblies = GetExplicitContractAssemblies(compilation);
        if (explicitAssemblies is not null)
            return explicitAssemblies;

        var assemblyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
                continue;

            if (ReferencesSharpLinkSdk(assembly))
                assemblyNames.Add(assembly.Identity.Name);
        }

        return assemblyNames;
    }

    private static HashSet<string>? GetExplicitContractAssemblies(Compilation compilation)
    {
        HashSet<string>? assemblyNames = null;
        foreach (var attribute in compilation.Assembly.GetAttributes())
        {
            if (!IsAttribute(attribute, "SharpLink.Sdk", "SharpLinkRpcContractsAttribute"))
                continue;

            assemblyNames ??= new HashSet<string>(StringComparer.Ordinal);

            if (attribute.ConstructorArguments.Length == 0)
                continue;

            var argument = attribute.ConstructorArguments[0];
            if (argument.Kind != TypedConstantKind.Array)
                continue;

            foreach (var item in argument.Values)
            {
                if (item.Value is INamedTypeSymbol type && type.ContainingAssembly is { } containingAssembly)
                {
                    assemblyNames.Add(containingAssembly.Identity.Name);
                }
            }
        }

        return assemblyNames;
    }

    private static bool ReferencesSharpLinkSdk(IAssemblySymbol assembly)
    {
        foreach (var module in assembly.Modules)
        {
            foreach (var referencedAssembly in module.ReferencedAssemblySymbols)
            {
                if (string.Equals(referencedAssembly.Name, "SharpLink.Sdk", StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private static void CollectReferencedInterfaces(
        INamespaceSymbol namespaceSymbol,
        ImmutableArray<RpcInterfaceModel>.Builder models,
        HashSet<string> seen)
    {
        foreach (var type in namespaceSymbol.GetTypeMembers())
            CollectReferencedInterfaces(type, models, seen, containingTypesArePublic: true);

        foreach (var nestedNamespace in namespaceSymbol.GetNamespaceMembers())
            CollectReferencedInterfaces(nestedNamespace, models, seen);
    }

    private static void CollectReferencedInterfaces(
        INamedTypeSymbol typeSymbol,
        ImmutableArray<RpcInterfaceModel>.Builder models,
        HashSet<string> seen,
        bool containingTypesArePublic)
    {
        var isPubliclyReachable = containingTypesArePublic && IsPubliclyReachableType(typeSymbol);
        if (isPubliclyReachable &&
            typeSymbol.TypeKind == TypeKind.Interface &&
            HasRpcContractAttribute(typeSymbol) &&
            InheritsIService(typeSymbol) &&
            !HasInvalidRpcMethod(typeSymbol))
        {
            var fullName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (seen.Add(fullName))
                models.Add(CreateInterfaceModel(typeSymbol));
        }

        if (!isPubliclyReachable)
            return;

        foreach (var nested in typeSymbol.GetTypeMembers())
            CollectReferencedInterfaces(nested, models, seen, containingTypesArePublic: isPubliclyReachable);
    }

    private static void CollectReferencedServices(
        INamespaceSymbol namespaceSymbol,
        ImmutableArray<RpcServiceModel>.Builder models,
        HashSet<string> seen)
    {
        foreach (var type in namespaceSymbol.GetTypeMembers())
            CollectReferencedServices(type, models, seen, containingTypesArePublic: true);

        foreach (var nestedNamespace in namespaceSymbol.GetNamespaceMembers())
            CollectReferencedServices(nestedNamespace, models, seen);
    }

    private static void CollectReferencedServices(
        INamedTypeSymbol typeSymbol,
        ImmutableArray<RpcServiceModel>.Builder models,
        HashSet<string> seen,
        bool containingTypesArePublic)
    {
        var isPubliclyReachable = containingTypesArePublic && IsPubliclyReachableType(typeSymbol);
        if (isPubliclyReachable &&
            typeSymbol.TypeKind == TypeKind.Class &&
            !typeSymbol.IsAbstract &&
            typeSymbol.GetAttributes().Any(IsRpcServiceAttribute))
        {
            var interfaceSymbol = FindRpcContractInterface(typeSymbol);
            if (interfaceSymbol is not null && !HasInvalidRpcMethod(interfaceSymbol))
            {
                var constructor = SelectServiceConstructor(typeSymbol);
                if (constructor is not null && IsServiceConstructorSupported(constructor, out _))
                {
                    var fullName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    if (seen.Add(fullName))
                    {
                        var ns = typeSymbol.ContainingNamespace.IsGlobalNamespace ? "" : typeSymbol.ContainingNamespace.ToDisplayString();
                        var parameters = constructor.Parameters.Select(static parameter => new RpcConstructorParameterModel(
                            parameter.Name,
                            parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))).ToImmutableArray();
                        models.Add(new RpcServiceModel(
                            typeSymbol.Name,
                            ns,
                            fullName,
                            CreateInterfaceModel(interfaceSymbol),
                            GetServiceLifetime(typeSymbol, out _),
                            parameters,
                            ImmutableArray.Create(interfaceSymbol.ContainingAssembly.Identity.ToString()),
                            typeSymbol.Locations.FirstOrDefault()));
                    }
                }
            }
        }

        if (!isPubliclyReachable)
            return;

        foreach (var nested in typeSymbol.GetTypeMembers())
            CollectReferencedServices(nested, models, seen, containingTypesArePublic: isPubliclyReachable);
    }

    private static bool IsPubliclyReachableType(INamedTypeSymbol typeSymbol)
        => typeSymbol.DeclaredAccessibility == Accessibility.Public;

    private static bool HasRpcContractAttribute(INamedTypeSymbol symbol)
        => symbol.GetAttributes().Any(static a => IsAttribute(a, "SharpLink.Sdk", "RpcContractAttribute"));

    private static INamedTypeSymbol? FindRpcContractInterface(INamedTypeSymbol serviceSymbol)
        => serviceSymbol.AllInterfaces.FirstOrDefault(HasRpcContractAttribute);

    private static bool IsAttribute(AttributeData attribute, string ns, string name)
    {
        if (attribute.AttributeClass is not { } attrClass)
            return false;
        if (!string.Equals(attrClass.Name, name, StringComparison.Ordinal))
            return false;
        return string.Equals(attrClass.ContainingNamespace.ToDisplayString(), ns, StringComparison.Ordinal);
    }

    private static string GetProxyHintName(RpcInterfaceModel model)
    {
        var fullName = model.FullName;
        if (fullName.StartsWith("global::", StringComparison.Ordinal))
            fullName = fullName.Substring("global::".Length);
        var name = new StringBuilder(fullName.Length + 16);
        foreach (var ch in fullName)
            name.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        name.Append('_').Append(unchecked((ulong)model.Hash).ToString("X16", InvariantCulture)).Append("_Proxy.g.cs");
        return name.ToString();
    }

    private static string GetStubHintName(RpcInterfaceModel model)
    {
        var fullName = model.FullName;
        if (fullName.StartsWith("global::", StringComparison.Ordinal))
            fullName = fullName.Substring("global::".Length);
        var name = new StringBuilder(fullName.Length + 16);
        foreach (var ch in fullName)
            name.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        name.Append('_').Append(unchecked((ulong)model.Hash).ToString("X16", InvariantCulture)).Append("_Stub.g.cs");
        return name.ToString();
    }

    private static string GetProxyArtifactHintName(RpcInterfaceModel model)
    {
        var fullName = model.FullName;
        if (fullName.StartsWith("global::", StringComparison.Ordinal))
            fullName = fullName.Substring("global::".Length);
        var name = new StringBuilder(fullName.Length + 16);
        foreach (var ch in fullName)
            name.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        name.Append('_').Append(unchecked((ulong)model.Hash).ToString("X16", InvariantCulture)).Append("_ProxyImpl.g.cs");
        return name.ToString();
    }

    private static string GetGeneratedContractName(INamedTypeSymbol symbol)
    {
        if (symbol.ContainingType is null)
            return symbol.Name;

        var parts = new Stack<string>();
        for (var current = symbol; current is not null; current = current.ContainingType)
            parts.Push(current.Name);
        var fullName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return string.Join("_", parts) + "_" + Hashing.GetSha256(fullName).Substring(0, 8);
    }

    private static string EscapeIdentifier(string identifier)
        => Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(identifier) != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None
            ? "@" + identifier
            : identifier;
}