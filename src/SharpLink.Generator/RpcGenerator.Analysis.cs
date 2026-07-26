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

        var contracts = symbol.AllInterfaces.Where(HasRpcContractAttribute).ToArray();
        if (contracts.Length != 1 || symbol.IsAbstract || symbol.IsGenericType)
            return null;
        var interfaceSymbol = contracts[0];
        if (HasInvalidRpcMethod(interfaceSymbol)) return null;

        var constructor = SelectServiceConstructor(symbol);
        if (constructor is null)
            return null;

        var lifetime = GetServiceLifetime(symbol, out var validLifetime);
        if (!validLifetime)
            return null;

        var ns = symbol.ContainingNamespace.IsGlobalNamespace ? "" : symbol.ContainingNamespace.ToDisplayString();
        var fullName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var parameters = constructor.Parameters
            .Select(static parameter => new RpcConstructorParameterModel(
                parameter.Name,
                parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)))
            .ToImmutableArray();
        // Runtime module dependencies describe generated RPC artifacts, not the
        // assemblies which happen to contain ordinary DI constructor services.
        var assemblyDependencies = new[] { interfaceSymbol.ContainingAssembly?.Identity.ToString() }
            .Where(static identity => !string.IsNullOrEmpty(identity))
            .Select(static identity => identity!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static identity => identity, StringComparer.Ordinal)
            .ToImmutableArray();
        return new RpcServiceModel(
            symbol.Name,
            ns,
            fullName,
            CreateInterfaceModel(interfaceSymbol),
            lifetime,
            parameters,
            assemblyDependencies,
            symbol.Locations.FirstOrDefault());
    }

    private static IMethodSymbol? SelectServiceConstructor(INamedTypeSymbol symbol)
    {
        var constructors = symbol.InstanceConstructors
            .Where(static constructor => constructor.DeclaredAccessibility == Accessibility.Public)
            .ToArray();
        var markedConstructors = constructors
            .Where(static constructor => constructor.GetAttributes().Any(static attribute =>
                IsAttribute(attribute, "Microsoft.Extensions.DependencyInjection", "ActivatorUtilitiesConstructorAttribute")))
            .ToArray();
        return markedConstructors.Length == 1
            ? markedConstructors[0]
            : constructors.Length == 1 ? constructors[0] : null;
    }

    private static RpcServiceDiagnosticModel? GetRpcServiceDiagnosticOrNull(
        GeneratorAttributeSyntaxContext context,
        CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Class)
            return null;

        var location = symbol.Locations.FirstOrDefault();
        var contracts = symbol.AllInterfaces.Where(HasRpcContractAttribute).ToArray();
        if (contracts.Length == 0)
        {
            return new RpcServiceDiagnosticModel(
                RpcServiceDiagnosticKind.MissingContract,
                symbol.Name,
                "the service does not implement an interface annotated with [RpcContract]",
                location);
        }
        if (contracts.Length > 1)
        {
            return new RpcServiceDiagnosticModel(
                RpcServiceDiagnosticKind.MultipleContracts,
                symbol.Name,
                $"the service implements {contracts.Length} RPC contracts; exactly one is supported",
                location);
        }
        if (symbol.IsAbstract || symbol.IsGenericType)
        {
            return new RpcServiceDiagnosticModel(
                RpcServiceDiagnosticKind.InvalidType,
                symbol.Name,
                symbol.IsAbstract ? "abstract RPC services are not supported" : "open generic RPC services are not supported",
                location);
        }

        var invalidLifetime = GetServiceLifetime(symbol, out var validLifetime);
        if (!validLifetime)
        {
            return new RpcServiceDiagnosticModel(
                RpcServiceDiagnosticKind.InvalidLifetime,
                symbol.Name,
                $"Lifetime value '{invalidLifetime}' must be Singleton, Connection, or Call",
                location);
        }

        var constructors = symbol.InstanceConstructors
            .Where(static constructor => constructor.DeclaredAccessibility == Accessibility.Public)
            .ToArray();
        var markedConstructors = constructors
            .Where(static constructor => constructor.GetAttributes().Any(static attribute =>
                IsAttribute(attribute, "Microsoft.Extensions.DependencyInjection", "ActivatorUtilitiesConstructorAttribute")))
            .ToArray();
        if (markedConstructors.Length > 1 ||
            (markedConstructors.Length == 0 && constructors.Length != 1))
        {
            return new RpcServiceDiagnosticModel(
                RpcServiceDiagnosticKind.InvalidConstructor,
                symbol.Name,
                constructors.Length == 0
                    ? "no public constructor can be called by the generated activator"
                    : "constructor selection is ambiguous; expose one public constructor or mark exactly one with [ActivatorUtilitiesConstructor]",
                location);
        }

        return null;
    }

    private static string GetServiceLifetime(INamedTypeSymbol symbol, out bool valid)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (!IsRpcServiceAttribute(attribute))
                continue;
            foreach (var argument in attribute.NamedArguments)
            {
                if (!string.Equals(argument.Key, "Lifetime", StringComparison.Ordinal) || argument.Value.Value is null)
                    continue;
                var value = Convert.ToInt32(argument.Value.Value, CultureInfo.InvariantCulture);
                valid = value is >= 0 and <= 2;
                return value switch
                {
                    1 => "Connection",
                    2 => "Call",
                    0 => "Singleton",
                    _ => value.ToString(CultureInfo.InvariantCulture)
                };
            }
        }

        valid = true;
        return "Singleton";
    }

    private static ImmutableArray<InvalidRpcMethodModel> GetInvalidRpcMethods(GeneratorAttributeSyntaxContext context, CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface)
            return ImmutableArray<InvalidRpcMethodModel>.Empty;
        if (!InheritsIService(symbol))
            return ImmutableArray<InvalidRpcMethodModel>.Empty;

        var list = ImmutableArray.CreateBuilder<InvalidRpcMethodModel>();
        foreach (var method in GetContractMethods(symbol))
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
        foreach (var method in GetContractMethods(symbol))
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
        foreach (var method in GetContractMethods(symbol))
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

    private static ImmutableArray<NonCancellableRpcMethodModel> GetNonCancellableRpcMethods(
        GeneratorAttributeSyntaxContext context,
        CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface ||
            !InheritsIService(symbol))
        {
            return ImmutableArray<NonCancellableRpcMethodModel>.Empty;
        }

        var list = ImmutableArray.CreateBuilder<NonCancellableRpcMethodModel>();
        foreach (var method in GetContractMethods(symbol))
        {
            if (method.Parameters.Any(IsCancellationTokenParameter) ||
                method.GetAttributes().Any(IsNonCancellableAttribute) ||
                IsStreamingMethod(method))
            {
                continue;
            }

            list.Add(new NonCancellableRpcMethodModel(method.Name, method.Locations.FirstOrDefault()));
        }
        return list.ToImmutable();
    }

    private static ImmutableArray<StreamingWithoutCancellationModel> GetStreamingWithoutCancellationMethods(
        GeneratorAttributeSyntaxContext context,
        CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface ||
            !InheritsIService(symbol))
        {
            return ImmutableArray<StreamingWithoutCancellationModel>.Empty;
        }

        var list = ImmutableArray.CreateBuilder<StreamingWithoutCancellationModel>();
        foreach (var method in GetContractMethods(symbol))
        {
            if (!IsStreamingMethod(method) ||
                method.Parameters.Any(IsCancellationTokenParameter) ||
                method.GetAttributes().Any(IsNonCancellableAttribute))
            {
                continue;
            }

            list.Add(new StreamingWithoutCancellationModel(method.Name, method.Locations.FirstOrDefault()));
        }
        return list.ToImmutable();
    }

    private static ImmutableArray<ConflictingCancellationContractModel> GetConflictingCancellationContractMethods(
        GeneratorAttributeSyntaxContext context,
        CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface ||
            !InheritsIService(symbol))
        {
            return ImmutableArray<ConflictingCancellationContractModel>.Empty;
        }

        var list = ImmutableArray.CreateBuilder<ConflictingCancellationContractModel>();
        foreach (var method in GetContractMethods(symbol))
        {
            if (!method.Parameters.Any(IsCancellationTokenParameter) ||
                !method.GetAttributes().Any(IsNonCancellableAttribute))
            {
                continue;
            }

            list.Add(new ConflictingCancellationContractModel(method.Name, method.Locations.FirstOrDefault()));
        }
        return list.ToImmutable();
    }

    private static bool IsStreamingMethod(IMethodSymbol method)
        => IsAsyncEnumerable(method.ReturnType, out _) ||
           method.Parameters.Any(static parameter => IsAsyncEnumerable(parameter.Type, out _));

    private static ImmutableArray<InvalidCallOptionsMethodModel> GetInvalidCallOptionsMethods(
        GeneratorAttributeSyntaxContext context,
        CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface ||
            !InheritsIService(symbol))
            return ImmutableArray<InvalidCallOptionsMethodModel>.Empty;

        var list = ImmutableArray.CreateBuilder<InvalidCallOptionsMethodModel>();
        foreach (var method in GetContractMethods(symbol))
        {
            if (method.Parameters.Count(IsCallOptionsParameter) <= 1)
                continue;
            list.Add(new InvalidCallOptionsMethodModel(method.Name, method.Locations.FirstOrDefault()));
        }
        return list.ToImmutable();
    }

    private static ImmutableArray<InvalidControlParameterOrderModel> GetInvalidControlParameterOrderMethods(
        GeneratorAttributeSyntaxContext context,
        CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Interface ||
            !InheritsIService(symbol))
            return ImmutableArray<InvalidControlParameterOrderModel>.Empty;

        var list = ImmutableArray.CreateBuilder<InvalidControlParameterOrderModel>();
        foreach (var method in GetContractMethods(symbol))
        {
            var optionsIndex = -1;
            var cancellationIndex = -1;
            for (var index = 0; index < method.Parameters.Length; index++)
            {
                if (IsCallOptionsParameter(method.Parameters[index]))
                    optionsIndex = index;
                if (IsCancellationTokenParameter(method.Parameters[index]))
                    cancellationIndex = index;
            }

            var expectedCancellationIndex = cancellationIndex >= 0 ? method.Parameters.Length - 1 : -1;
            var expectedOptionsIndex = optionsIndex >= 0
                ? method.Parameters.Length - (cancellationIndex >= 0 ? 2 : 1)
                : -1;
            if (cancellationIndex == expectedCancellationIndex && optionsIndex == expectedOptionsIndex)
                continue;
            list.Add(new InvalidControlParameterOrderModel(method.Name, method.Locations.FirstOrDefault()));
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

        foreach (var method in GetContractMethods(symbol))
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

        return GetContractMethods(interfaceSymbol)
            .Any(m =>
                !IsSupportedRpcReturnType(m.ReturnType) ||
                m.IsGenericMethod ||
                HasTypeParameter(m.ReturnType) ||
                m.Parameters.Any(p => HasTypeParameter(p.Type)) ||
                m.Parameters.Count(IsCancellationTokenParameter) > 1 ||
                m.Parameters.Count(IsCallOptionsParameter) > 1 ||
                !HasValidControlParameterOrder(m) ||
                m.Parameters.Count(p => IsAsyncEnumerable(p.Type, out _)) > sbyte.MaxValue ||
                false);
    }

    private static bool HasTypeParameter(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.TypeParameter)
            return true;

        return type switch
        {
            IArrayTypeSymbol arrayType => HasTypeParameter(arrayType.ElementType),
            IPointerTypeSymbol pointerType => HasTypeParameter(pointerType.PointedAtType),
            INamedTypeSymbol namedType => namedType.IsUnboundGenericType ||
                                         namedType.TypeArguments.Any(HasTypeParameter),
            _ => false
        };
    }

    private static bool IsCancellationTokenParameter(IParameterSymbol parameter)
        => parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::System.Threading.CancellationToken";

    private static bool IsCallOptionsParameter(IParameterSymbol parameter)
        => parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::SharpLink.Sdk.SharpLinkCallOptions";

    private static bool HasValidControlParameterOrder(IMethodSymbol method)
    {
        var controls = method.Parameters.Where(p => IsCancellationTokenParameter(p) || IsCallOptionsParameter(p)).ToArray();
        if (controls.Length == 0)
            return true;
        var firstControl = method.Parameters.Length - controls.Length;
        for (var index = firstControl; index < method.Parameters.Length; index++)
        {
            if (!IsCancellationTokenParameter(method.Parameters[index]) && !IsCallOptionsParameter(method.Parameters[index]))
                return false;
        }
        return !method.Parameters.Any(IsCancellationTokenParameter) ||
               IsCancellationTokenParameter(method.Parameters[method.Parameters.Length - 1]);
    }

    private static bool InheritsIService(INamedTypeSymbol symbol)
        => symbol.AllInterfaces.Any(IsIService);

    private static IEnumerable<IMethodSymbol> GetContractMethods(INamedTypeSymbol symbol)
    {
        var methods = new List<IMethodSymbol>();
        foreach (var method in symbol.GetMembers().OfType<IMethodSymbol>()
                     .Where(static method => method.MethodKind == MethodKind.Ordinary))
        {
            methods.Add(method);
        }

        foreach (var method in symbol.AllInterfaces
                     .Where(static contract => !IsIService(contract))
                     .OrderBy(static contract => contract.ToDisplayString(), StringComparer.Ordinal)
                     .SelectMany(static contract => contract.GetMembers()
                         .OfType<IMethodSymbol>()
                         .Where(static method => method.MethodKind == MethodKind.Ordinary)))
        {
            if (!methods.Any(existing => HasSameContractSignature(existing, method)))
                methods.Add(method);
        }

        return methods;
    }

    private static bool HasSameContractSignature(IMethodSymbol left, IMethodSymbol right)
    {
        if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal) ||
            left.Arity != right.Arity ||
            left.Parameters.Length != right.Parameters.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Parameters.Length; index++)
        {
            var leftParameter = left.Parameters[index];
            var rightParameter = right.Parameters[index];
            if (leftParameter.RefKind != rightParameter.RefKind ||
                !SymbolEqualityComparer.Default.Equals(leftParameter.Type, rightParameter.Type))
            {
                return false;
            }
        }

        return true;
    }

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

    private static bool IsIdempotentAttribute(AttributeData attribute)
    {
        return IsAttribute(attribute, "SharpLink.Sdk", "IdempotentAttribute") ||
               IsAttribute(attribute, "SharpLink.Abstractions", "IdempotentAttribute");
    }

    private static bool IsNonCancellableAttribute(AttributeData attribute)
    {
        return IsAttribute(attribute, "SharpLink.Sdk", "NonCancellableAttribute") ||
               IsAttribute(attribute, "SharpLink.Abstractions", "NonCancellableAttribute");
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

        var methods = GetContractMethods(symbol)
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
                var isIdempotent = m.GetAttributes().Any(IsIdempotentAttribute);
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
                    var payloadType = isStream ? pItemType! : p.Type;
                    var isCancellationToken = IsCancellationTokenParameter(p);
                    var isCallOptions = IsCallOptionsParameter(p);
                    return new RpcParameterModel(
                        p.Name,
                        pType,
                        isStream,
                        isStream ? pItemType!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) : null,
                        IsInlineFixedRpcType(p.Type),
                        isValueType,
                        isNullableReference,
                        IsNullablePayload(payloadType),
                        isCancellationToken,
                        isCallOptions,
                        GetEnumUnderlyingType(p.Type),
                        pItemType is null ? null : GetEnumUnderlyingType(pItemType),
                        p.Locations.FirstOrDefault());
                }).ToImmutableArray();

                var paramTypes = paramArray
                    .Where(p => !p.IsCancellationToken && !p.IsCallOptions)
                    .Select(p => p.Type)
                    .ToArray();
                var methodHash = Hashing.GetMethodHash(m.Name, paramTypes);

                var requestSchema = string.Join(";", paramArray
                    .Where(static parameter => !parameter.IsCancellationToken && !parameter.IsCallOptions)
                    .Select(static parameter =>
                        $"{parameter.Name}:{parameter.Type}:{(parameter.IsStream ? "stream" : "value")}:{(parameter.PayloadNullable ? "nullable" : "required")}"));
                var responseSchema = isStreamReturn
                    ? $"stream:{streamItemType}"
                    : $"value:{returnType}";
                var kind = isOneWay ? "OneWay" : isStreamReturn
                    ? (paramArray.Any(static parameter => parameter.IsStream) ? "DuplexStreaming" : "ServerStreaming")
                    : paramArray.Any(static parameter => parameter.IsStream) ? "ClientStreaming" : "Unary";
                var canonical = $"{m.Name}|{methodHash}|{kind}|{requestSchema}|{responseSchema}|cancel={paramArray.Any(static parameter => parameter.IsCancellationToken)}|timeout={hasTimeoutAttribute}:{timeoutSeconds?.ToString("R", CultureInfo.InvariantCulture)}|idempotent={isIdempotent}";
                var responsePayload = isGenericTask
                    ? ((INamedTypeSymbol)m.ReturnType).TypeArguments[0]
                    : itemTypeSymbol;

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
                    HasCallOptions: paramArray.Any(p => p.IsCallOptions),
                    HasTimeoutAttribute: hasTimeoutAttribute,
                    TimeoutSeconds: timeoutSeconds,
                    IsIdempotent: isIdempotent,
                    Hash: methodHash,
                    Parameters: paramArray,
                    RequestSchema: requestSchema,
                    ResponseSchema: responseSchema,
                    Fingerprint: Hashing.GetSha256(canonical),
                    ResponseNullable: responsePayload is not null && IsNullablePayload(responsePayload),
                    ResponseEnumUnderlyingType: responsePayload is null ? null : GetEnumUnderlyingType(responsePayload),
                    StreamItemEnumUnderlyingType: itemTypeSymbol is null ? null : GetEnumUnderlyingType(itemTypeSymbol),
                    Location: m.Locations.FirstOrDefault());
            }).ToImmutableArray();

        var fullname = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var interfaceHash = Hashing.GetInterfaceHash(fullname);
        var canonicalContract = $"{fullname}|{interfaceHash}|" + string.Join("|", methods
            .OrderBy(static method => method.Hash)
            .Select(static method => method.Fingerprint));
        var dependencyTypes = GetContractMethods(symbol)
            .SelectMany(static method => method.Parameters.Select(static parameter => parameter.Type)
                .Append(method.ReturnType));
        return new RpcInterfaceModel(
            symbol.Name,
            ns,
            fullname,
            interfaceHash,
            methods,
            Hashing.GetSha256(canonicalContract),
            GetArtifactAssemblyDependencies(symbol.ContainingAssembly, dependencyTypes),
            symbol.Locations.FirstOrDefault());
    }

    private static string? GetEnumUnderlyingType(ITypeSymbol type)
        => type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying }
            ? underlying.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            : null;

    private static bool IsInlineFixedRpcType(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum)
            return true;
        if (type.SpecialType is SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte or
            SpecialType.System_Int16 or SpecialType.System_UInt16 or
            SpecialType.System_Char or SpecialType.System_Int32 or SpecialType.System_UInt32 or
            SpecialType.System_Single or SpecialType.System_Int64 or SpecialType.System_UInt64 or
            SpecialType.System_Double)
        {
            return true;
        }

        return type.ToDisplayString() is "System.Half" or "System.Guid" or
            "System.TimeSpan" or "System.Int128" or "System.UInt128";
    }

    private static bool IsNullablePayload(ITypeSymbol type)
        => type.NullableAnnotation == NullableAnnotation.Annotated ||
           type is INamedTypeSymbol named &&
           named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

    private static ImmutableArray<string> GetArtifactAssemblyDependencies(
        IAssemblySymbol owner,
        IEnumerable<ITypeSymbol> types)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in types)
            CollectArtifactAssemblyDependencies(owner, type, identities);
        return identities.OrderBy(static identity => identity, StringComparer.Ordinal).ToImmutableArray();
    }

    private static void CollectArtifactAssemblyDependencies(
        IAssemblySymbol owner,
        ITypeSymbol type,
        HashSet<string> identities)
    {
        if (type is IArrayTypeSymbol array)
        {
            CollectArtifactAssemblyDependencies(owner, array.ElementType, identities);
            return;
        }
        if (type is not INamedTypeSymbol named)
            return;

        var assembly = named.ContainingAssembly;
        if (assembly is not null &&
            !SymbolEqualityComparer.Default.Equals(assembly, owner) &&
            ReferencesSharpLinkSdk(assembly))
        {
            identities.Add(assembly.Identity.ToString());
        }
        foreach (var argument in named.TypeArguments)
            CollectArtifactAssemblyDependencies(owner, argument, identities);
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
                    var constructor = typeSymbol.InstanceConstructors.FirstOrDefault(static candidate =>
                        candidate.DeclaredAccessibility == Accessibility.Public);
                    var parameters = constructor is null
                        ? ImmutableArray<RpcConstructorParameterModel>.Empty
                        : constructor.Parameters.Select(static parameter => new RpcConstructorParameterModel(
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

    private static string GetStubHintName(RpcInterfaceModel model)
    {
        var fullName = model.FullName;
        if (fullName.StartsWith("global::", StringComparison.Ordinal))
            fullName = fullName.Substring("global::".Length);
        var name = new StringBuilder(fullName.Length + 16);
        foreach (var ch in fullName)
            name.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        name.Append("_Stub.g.cs");
        return name.ToString();
    }

}
