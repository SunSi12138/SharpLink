namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static bool IsAsyncEnumerable(ITypeSymbol type, out ITypeSymbol? itemType)
    {
        itemType = null;
        if (type is not INamedTypeSymbol named || named.OriginalDefinition.ToDisplayString() != "System.Collections.Generic.IAsyncEnumerable<T>") 
            return false;
        itemType = named.TypeArguments[0];
        return true;
    }

    private static RpcInterfaceModel? GetInterfaceModelOrNull(GeneratorAttributeSyntaxContext context, CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface)
            return null;
        if (!InheritsIService(symbol))
            return null;

        return HasInvalidRpcMethod(symbol) ? null : CreateInterfaceModel(symbol);
    }

    private static RpcServiceModel? GetServiceModelOrNull(GeneratorAttributeSyntaxContext context, CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Class)
            return null;

        var interfaceSymbol = FindRpcContractInterface(symbol);
        if (interfaceSymbol == null) return null;
        if (HasInvalidRpcMethod(interfaceSymbol)) return null;

        var ns = symbol.ContainingNamespace.IsGlobalNamespace ? "" : symbol.ContainingNamespace.ToDisplayString();
        var fullName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return new RpcServiceModel(symbol.Name, ns, fullName, CreateInterfaceModel(interfaceSymbol));
    }

    private static ImmutableArray<InvalidRpcMethodModel> GetInvalidRpcMethods(GeneratorAttributeSyntaxContext context, CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface)
            return ImmutableArray<InvalidRpcMethodModel>.Empty;
        if (!InheritsIService(symbol))
            return ImmutableArray<InvalidRpcMethodModel>.Empty;

        var list = ImmutableArray.CreateBuilder<InvalidRpcMethodModel>();
        foreach (var method in symbol.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary))
        {
            if (IsSupportedRpcReturnType(method.ReturnType))
                continue;

            list.Add(new InvalidRpcMethodModel(
                method.Name,
                method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                method.Locations.FirstOrDefault()));
        }

        return list.ToImmutable();
    }

    private static ImmutableArray<InvalidCancellationTokenMethodModel> GetInvalidCancellationTokenMethods(GeneratorAttributeSyntaxContext context, CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface)
            return ImmutableArray<InvalidCancellationTokenMethodModel>.Empty;
        if (!InheritsIService(symbol))
            return ImmutableArray<InvalidCancellationTokenMethodModel>.Empty;

        var list = ImmutableArray.CreateBuilder<InvalidCancellationTokenMethodModel>();
        foreach (var method in symbol.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary))
        {
            var cancellationTokenCount = method.Parameters.Count(IsCancellationTokenParameter);
            if (cancellationTokenCount <= 1)
                continue;

            list.Add(new InvalidCancellationTokenMethodModel(
                method.Name,
                method.Locations.FirstOrDefault()));
        }

        return list.ToImmutable();
    }

    private static ImmutableArray<InvalidStreamCountMethodModel> GetInvalidStreamCountMethods(GeneratorAttributeSyntaxContext context, CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface)
            return ImmutableArray<InvalidStreamCountMethodModel>.Empty;
        if (!InheritsIService(symbol))
            return ImmutableArray<InvalidStreamCountMethodModel>.Empty;

        var list = ImmutableArray.CreateBuilder<InvalidStreamCountMethodModel>();
        foreach (var method in symbol.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary))
        {
            var streamCount = method.Parameters.Count(p => IsAsyncEnumerable(p.Type, out var _));
            if (streamCount <= sbyte.MaxValue)
                continue;

            list.Add(new InvalidStreamCountMethodModel(
                method.Name,
                streamCount,
                method.Locations.FirstOrDefault()));
        }

        return list.ToImmutable();
    }

    private static ImmutableArray<InvalidTimeoutCancellationMethodModel> GetInvalidTimeoutCancellationMethods(GeneratorAttributeSyntaxContext context, CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface)
            return ImmutableArray<InvalidTimeoutCancellationMethodModel>.Empty;
        if (!InheritsIService(symbol))
            return ImmutableArray<InvalidTimeoutCancellationMethodModel>.Empty;

        var list = ImmutableArray.CreateBuilder<InvalidTimeoutCancellationMethodModel>();
        foreach (var method in symbol.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary))
        {
            var hasTimeout = method.GetAttributes().Any(IsTimeoutAttribute);
            if (!hasTimeout)
                continue;

            var hasCancellationToken = method.Parameters.Any(IsCancellationTokenParameter);
            if (hasCancellationToken)
                continue;

            list.Add(new InvalidTimeoutCancellationMethodModel(
                method.Name,
                method.Locations.FirstOrDefault()));
        }

        return list.ToImmutable();
    }

    private static ImmutableArray<InvalidGenericUsageModel> GetInvalidGenericUsage(GeneratorAttributeSyntaxContext context, CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface)
            return ImmutableArray<InvalidGenericUsageModel>.Empty;
        if (!InheritsIService(symbol))
            return ImmutableArray<InvalidGenericUsageModel>.Empty;

        var list = ImmutableArray.CreateBuilder<InvalidGenericUsageModel>();
        if (symbol.Arity > 0)
        {
            list.Add(new InvalidGenericUsageModel(
                symbol.Name,
                symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                symbol.Locations.FirstOrDefault()));
        }

        foreach (var method in symbol.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary))
        {
            var hasGenericUsage = method.IsGenericMethod ||
                                  HasTypeParameter(method.ReturnType) ||
                                  method.Parameters.Any(p => HasTypeParameter(p.Type));
            if (!hasGenericUsage)
                continue;

            list.Add(new InvalidGenericUsageModel(
                method.Name,
                method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                method.Locations.FirstOrDefault()));
        }

        return list.ToImmutable();
    }

    private static InvalidRpcContractInheritanceModel? GetInvalidRpcContractInheritance(
        GeneratorAttributeSyntaxContext context,
        CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface)
            return null;
        if (InheritsIService(symbol))
            return null;

        return new InvalidRpcContractInheritanceModel(
            symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            symbol.Locations.FirstOrDefault());
    }

    private static bool HasInvalidRpcMethod(INamedTypeSymbol interfaceSymbol)
    {
        if (interfaceSymbol.Arity > 0)
            return true;

        return interfaceSymbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary)
            .Any(m =>
                !IsSupportedRpcReturnType(m.ReturnType) ||
                m.IsGenericMethod ||
                HasTypeParameter(m.ReturnType) ||
                m.Parameters.Any(p => HasTypeParameter(p.Type)) ||
                m.Parameters.Count(IsCancellationTokenParameter) > 1 ||
                m.Parameters.Count(p => IsAsyncEnumerable(p.Type, out _)) > sbyte.MaxValue ||
                (m.GetAttributes().Any(IsTimeoutAttribute) && !m.Parameters.Any(IsCancellationTokenParameter)));
    }

    private static bool HasTypeParameter(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.TypeParameter)
            return true;

        return type switch
        {
            IArrayTypeSymbol arrayType => HasTypeParameter(arrayType.ElementType),
            IPointerTypeSymbol pointerType => HasTypeParameter(pointerType.PointedAtType),
            INamedTypeSymbol namedType => namedType.TypeArguments.Any(HasTypeParameter),
            _ => false
        };
    }

    private static bool IsCancellationTokenParameter(IParameterSymbol parameter)
        => parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::System.Threading.CancellationToken";

    private static bool InheritsIService(INamedTypeSymbol symbol)
        => symbol.AllInterfaces.Any(IsIService);

    private static bool IsIService(INamedTypeSymbol symbol)
        => string.Equals(symbol.Name, "IService", StringComparison.Ordinal) &&
           string.Equals(symbol.ContainingNamespace.ToDisplayString(), "SharpLink.Sdk", StringComparison.Ordinal);

    private static bool IsRpcServiceAttribute(AttributeData attribute)
    {
        return IsAttribute(attribute, "SharpLink.Sdk", "RpcServiceAttribute") ||
               IsAttribute(attribute, "SharpLink.Abstractions", "RpcServiceAttribute");
    }

    private static bool IsOnewayAttribute(AttributeData attribute)
    {
        return IsAttribute(attribute, "SharpLink.Sdk", "OnewayAttribute") ||
               IsAttribute(attribute, "SharpLink.Abstractions", "OnewayAttribute");
    }

    private static bool IsTimeoutAttribute(AttributeData attribute)
    {
        return IsAttribute(attribute, "SharpLink.Sdk", "TimeoutAttribute") ||
               IsAttribute(attribute, "SharpLink.Abstractions", "TimeoutAttribute");
    }

    private static double? GetTimeoutSecondsOrNull(IMethodSymbol method, out bool hasTimeoutAttribute)
    {
        hasTimeoutAttribute = false;
        foreach (var attribute in method.GetAttributes())
        {
            if (!IsTimeoutAttribute(attribute))
                continue;

            hasTimeoutAttribute = true;
            if (attribute.ConstructorArguments.Length == 0)
                return null;

            var argument = attribute.ConstructorArguments[0];
            if (argument.Value is null)
                return null;

            return argument.Value switch
            {
                double value => value,
                float value => value,
                int value => value,
                long value => value,
                _ => null
            };
        }

        return null;
    }

    private static bool IsSupportedRpcReturnType(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
            return false;

        var ns = named.ContainingNamespace.ToDisplayString();
        var original = named.OriginalDefinition;

        if (ns != "System.Threading.Tasks")
            return ns == "System.Collections.Generic" && original is { Name: "IAsyncEnumerable", Arity: 1 };
        return original switch
        {
            { Name: "Task", Arity: 0 or 1 } or { Name: "ValueTask", Arity: 0 or 1 } => true,
            _ => ns == "System.Collections.Generic" && original is { Name: "IAsyncEnumerable", Arity: 1 }
        };
    }

    private static RpcInterfaceModel CreateInterfaceModel(INamedTypeSymbol symbol)
    {
        var ns = symbol.ContainingNamespace.IsGlobalNamespace ? "" : symbol.ContainingNamespace.ToDisplayString();

        var methods = symbol.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary)
            .Select(m =>
            {
                var returnType = m.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var isGenericTask = m.ReturnType is INamedTypeSymbol { IsGenericType: true } &&
                                    m.ReturnType.ToDisplayString().StartsWith("System.Threading.Tasks");
                var genericArg = isGenericTask
                    ? ((INamedTypeSymbol)m.ReturnType).TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    : null;

                var isNonGenericTaskLike = m.ReturnType.ToDisplayString() is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask";
                var isOneWay = m.GetAttributes().Any(IsOnewayAttribute);
                var timeoutSeconds = GetTimeoutSecondsOrNull(m, out var hasTimeoutAttribute);

                var isStreamReturn = false;
                string? streamItemType = null;
                if (IsAsyncEnumerable(m.ReturnType, out var itemTypeSymbol))
                {
                    isStreamReturn = true;
                    streamItemType = itemTypeSymbol!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    isGenericTask = false;
                    genericArg = null;
                }

                var paramArray = m.Parameters.Select(p =>
                {
                    var pType = p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    var isStream = IsAsyncEnumerable(p.Type, out var pItemType);
                    var isValueType = p.Type.IsValueType;
                    var isNullableReference = !isValueType && p.NullableAnnotation == NullableAnnotation.Annotated;
                    var isCancellationToken = IsCancellationTokenParameter(p);
                    return new RpcParameterModel(
                        p.Name,
                        pType,
                        isStream,
                        isStream ? pItemType!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) : null,
                        p.Type.IsUnmanagedType,
                        isValueType,
                        isNullableReference,
                        isCancellationToken);
                }).ToImmutableArray();

                var paramTypes = paramArray.Select(p => p.Type).ToArray();
                var methodHash = Hashing.GetMethodHash(m.Name, paramTypes);

                return new RpcMethodModel(
                    Name: m.Name,
                    ReturnType: returnType,
                    IsGenericTask: isGenericTask,
                    IsStreamReturn: isStreamReturn,
                    StreamItemType: streamItemType,
                    GenericArgumentType: genericArg,
                    IsVoid: m.ReturnsVoid || isNonGenericTaskLike,
                    IsOneWay: isOneWay,
                    HasCancellationToken: paramArray.Any(p => p.IsCancellationToken),
                    HasTimeoutAttribute: hasTimeoutAttribute,
                    TimeoutSeconds: timeoutSeconds,
                    Hash: methodHash,
                    Parameters: paramArray);
            }).ToImmutableArray();

        var fullname = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return new RpcInterfaceModel(symbol.Name, ns, fullname, Hashing.GetInterfaceHash(fullname), methods);
    }

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

            if (attribute.ConstructorArguments.Length == 0)
                continue;

            var argument = attribute.ConstructorArguments[0];
            if (argument.Kind != TypedConstantKind.Array)
                continue;

            foreach (var item in argument.Values)
            {
                if (item.Value is INamedTypeSymbol type && type.ContainingAssembly is { } containingAssembly)
                {
                    assemblyNames ??= new HashSet<string>(StringComparer.Ordinal);
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
                var fullName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (seen.Add(fullName))
                {
                    var ns = typeSymbol.ContainingNamespace.IsGlobalNamespace ? "" : typeSymbol.ContainingNamespace.ToDisplayString();
                    models.Add(new RpcServiceModel(
                        typeSymbol.Name,
                        ns,
                        fullName,
                        CreateInterfaceModel(interfaceSymbol)));
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
        name.Append("_Proxy.g.cs");
        return name.ToString();
    }

    private static string GetStubHintName(RpcServiceModel model)
    {
        var fullName = model.ServiceFullName;
        if (fullName.StartsWith("global::", StringComparison.Ordinal))
            fullName = fullName.Substring("global::".Length);
        var name = new StringBuilder(fullName.Length + 16);
        foreach (var ch in fullName)
            name.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        name.Append("_Stub.g.cs");
        return name.ToString();
    }

}
