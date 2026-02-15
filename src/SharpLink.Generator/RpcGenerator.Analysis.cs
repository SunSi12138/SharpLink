namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static bool IsInterfaceCandidate(SyntaxNode node, CancellationToken _) => node is InterfaceDeclarationSyntax;
    private static bool IsClassCandidate(SyntaxNode node, CancellationToken _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 };

    private static bool IsAsyncEnumerable(ITypeSymbol type, out ITypeSymbol? itemType)
    {
        itemType = null;
        if (type is not INamedTypeSymbol named || named.OriginalDefinition.ToDisplayString() != "System.Collections.Generic.IAsyncEnumerable<T>") 
            return false;
        itemType = named.TypeArguments[0];
        return true;
    }

    private static RpcInterfaceModel? GetInterfaceModelOrNull(GeneratorSyntaxContext context, CancellationToken _)
    {
        var interfaceDecl = (InterfaceDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(interfaceDecl) is not INamedTypeSymbol symbol) return null;

        if (!symbol.AllInterfaces.Any(i => i.ToDisplayString() == "SharpLink.Sdk.IService"))
            return null;

        return HasInvalidRpcMethod(symbol) ? null : CreateInterfaceModel(symbol);
    }

    private static RpcServiceModel? GetServiceModelOrNull(GeneratorSyntaxContext context, CancellationToken _)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(classDecl) is not INamedTypeSymbol symbol) return null;

        if (!symbol.GetAttributes().Any(IsRpcServiceAttribute)) return null;

        var interfaceSymbol = symbol.AllInterfaces.FirstOrDefault(i =>
            i.ToDisplayString() != "SharpLink.Sdk.IService" &&
            i.AllInterfaces.Any(baseI => baseI.ToDisplayString() == "SharpLink.Sdk.IService"));
        if (interfaceSymbol == null) return null;
        if (HasInvalidRpcMethod(interfaceSymbol)) return null;

        var ns = symbol.ContainingNamespace.IsGlobalNamespace ? "" : symbol.ContainingNamespace.ToDisplayString();
        var fullName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return new RpcServiceModel(symbol.Name, ns, fullName, CreateInterfaceModel(interfaceSymbol));
    }

    private static ImmutableArray<InvalidRpcMethodModel> GetInvalidRpcMethods(GeneratorSyntaxContext context, CancellationToken _)
    {
        var interfaceDecl = (InterfaceDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(interfaceDecl) is not INamedTypeSymbol symbol)
            return ImmutableArray<InvalidRpcMethodModel>.Empty;

        if (!symbol.AllInterfaces.Any(i => i.ToDisplayString() == "SharpLink.Sdk.IService"))
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

    private static ImmutableArray<InvalidCancellationTokenMethodModel> GetInvalidCancellationTokenMethods(GeneratorSyntaxContext context, CancellationToken _)
    {
        var interfaceDecl = (InterfaceDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(interfaceDecl) is not INamedTypeSymbol symbol)
            return ImmutableArray<InvalidCancellationTokenMethodModel>.Empty;

        if (!symbol.AllInterfaces.Any(i => i.ToDisplayString() == "SharpLink.Sdk.IService"))
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

    private static ImmutableArray<InvalidStreamCountMethodModel> GetInvalidStreamCountMethods(GeneratorSyntaxContext context, CancellationToken _)
    {
        var interfaceDecl = (InterfaceDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(interfaceDecl) is not INamedTypeSymbol symbol || !symbol.AllInterfaces.Any(i => i.ToDisplayString() == "SharpLink.Sdk.IService"))
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

    private static ImmutableArray<InvalidTimeoutCancellationMethodModel> GetInvalidTimeoutCancellationMethods(GeneratorSyntaxContext context, CancellationToken _)
    {
        var interfaceDecl = (InterfaceDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(interfaceDecl) is not INamedTypeSymbol symbol)
            return ImmutableArray<InvalidTimeoutCancellationMethodModel>.Empty;

        if (!symbol.AllInterfaces.Any(i => i.ToDisplayString() == "SharpLink.Sdk.IService"))
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

    private static bool HasInvalidRpcMethod(INamedTypeSymbol interfaceSymbol)
    {
        return interfaceSymbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary)
            .Any(m =>
                !IsSupportedRpcReturnType(m.ReturnType) ||
                m.Parameters.Count(IsCancellationTokenParameter) > 1 ||
                m.Parameters.Count(p => IsAsyncEnumerable(p.Type, out _)) > sbyte.MaxValue ||
                (m.GetAttributes().Any(IsTimeoutAttribute) && !m.Parameters.Any(IsCancellationTokenParameter)));
    }

    private static bool IsCancellationTokenParameter(IParameterSymbol parameter)
        => parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::System.Threading.CancellationToken";

    private static bool IsRpcServiceAttribute(AttributeData attribute)
    {
        var fullName = attribute.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return fullName is "global::SharpLink.Sdk.RpcServiceAttribute" or "global::SharpLink.Abstractions.RpcServiceAttribute";
    }

    private static bool IsOnewayAttribute(AttributeData attribute)
    {
        var fullName = attribute.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return fullName is "global::SharpLink.Sdk.OnewayAttribute" or "global::SharpLink.Abstractions.OnewayAttribute";
    }

    private static bool IsTimeoutAttribute(AttributeData attribute)
    {
        var fullName = attribute.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return fullName is "global::SharpLink.Sdk.TimeoutAttribute" or "global::SharpLink.Abstractions.TimeoutAttribute";
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

}
