namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static RpcServiceModel? GetServiceModelOrNull(
        GeneratorAttributeSyntaxContext context,
        CancellationToken _)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || symbol.TypeKind != TypeKind.Class)
            return null;

        var contracts = symbol.AllInterfaces.Where(HasRpcContractAttribute).ToArray();
        if (contracts.Length != 1 || symbol.IsAbstract || symbol.IsGenericType ||
            !IsAccessibleFromGeneratedCode(symbol))
            return null;
        var interfaceSymbol = contracts[0];
        if (HasInvalidRpcMethod(interfaceSymbol)) return null;

        var constructor = SelectServiceConstructor(symbol);
        if (constructor is null ||
            !IsServiceConstructorSupported(constructor, out var ignoredConstructorDetail))
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

    private static bool IsServiceConstructorSupported(
        IMethodSymbol constructor,
        out string invalidDetail)
    {
        foreach (var parameter in constructor.Parameters)
        {
            if (parameter.RefKind is RefKind.Ref or RefKind.Out or RefKind.RefReadOnlyParameter)
            {
                invalidDetail =
                    $"constructor dependency '{parameter.Name}' requires by-reference storage and cannot be supplied by IServiceProvider";
                return false;
            }
            if (parameter.Type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer ||
                ContainsRefLikeType(parameter.Type))
            {
                invalidDetail =
                    $"constructor dependency '{parameter.Name}' has type '{parameter.Type.ToDisplayString()}', which cannot round-trip through IServiceProvider";
                return false;
            }
        }

        invalidDetail = string.Empty;
        return true;
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
        if (symbol.IsAbstract || symbol.IsGenericType || !IsAccessibleFromGeneratedCode(symbol))
        {
            return new RpcServiceDiagnosticModel(
                RpcServiceDiagnosticKind.InvalidType,
                symbol.Name,
                symbol.IsAbstract
                    ? "abstract RPC services are not supported"
                    : symbol.IsGenericType
                        ? "open generic RPC services are not supported"
                        : "the service type and every containing type must be accessible from generated code",
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

        var selectedConstructor = markedConstructors.Length == 1
            ? markedConstructors[0]
            : constructors[0];
        if (!IsServiceConstructorSupported(selectedConstructor, out var invalidConstructorDetail))
        {
            return new RpcServiceDiagnosticModel(
                RpcServiceDiagnosticKind.InvalidConstructor,
                symbol.Name,
                invalidConstructorDetail,
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
}
