namespace SharpLink.Generator;

public partial class RpcGenerator
{
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
                var returnType = GetTypeName(m.ReturnType);
                var displayReturnType = m.ReturnType.ToDisplayString(FullyQualifiedNullableFormat);
                var isGenericTask = m.ReturnType is INamedTypeSymbol { IsGenericType: true } &&
                                    m.ReturnType.ToDisplayString().StartsWith("System.Threading.Tasks");
                var genericArg = isGenericTask
                    ? GetTypeName(((INamedTypeSymbol)m.ReturnType).TypeArguments[0])
                    : null;
                var displayGenericArg = isGenericTask
                    ? ((INamedTypeSymbol)m.ReturnType).TypeArguments[0].ToDisplayString(FullyQualifiedNullableFormat)
                    : null;

                var isNonGenericTaskLike = m.ReturnType.ToDisplayString() is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask";
                var isOneWay = m.GetAttributes().Any(IsOnewayAttribute);
                var isIdempotent = m.GetAttributes().Any(IsIdempotentAttribute);
                var timeoutTicks = GetTimeoutTicksOrNull(m, out var hasTimeoutAttribute);

                var isStreamReturn = false;
                string? streamItemType = null;
                string? displayStreamItemType = null;
                if (IsAsyncEnumerable(m.ReturnType, out var itemTypeSymbol))
                {
                    isStreamReturn = true;
                    streamItemType = GetTypeName(itemTypeSymbol!);
                    displayStreamItemType = itemTypeSymbol!.ToDisplayString(FullyQualifiedNullableFormat);
                    isGenericTask = false;
                    genericArg = null;
                    displayGenericArg = null;
                }

                var paramArray = m.Parameters.Select(p =>
                {
                    var pType = GetTypeName(p.Type);
                    var displayPType = p.Type.ToDisplayString(FullyQualifiedNullableFormat);
                    var isStream = IsAsyncEnumerable(p.Type, out var pItemType);
                    var isValueType = p.Type.IsValueType;
                    var isNullableReference = !isValueType && p.NullableAnnotation == NullableAnnotation.Annotated;
                    var payloadType = isStream ? pItemType! : p.Type;
                    var isCancellationToken = IsCancellationTokenParameter(p);
                    return new RpcParameterModel(
                        p.Name,
                        pType,
                        displayPType,
                        isStream,
                        isStream ? GetTypeName(pItemType!) : null,
                        isStream ? pItemType!.ToDisplayString(FullyQualifiedNullableFormat) : null,
                        IsInlineFixedRpcType(p.Type),
                        isValueType,
                        isNullableReference,
                        IsNullablePayload(payloadType),
                        isCancellationToken,
                        GetEnumUnderlyingType(p.Type),
                        pItemType is null ? null : GetEnumUnderlyingType(pItemType),
                        p.Locations.FirstOrDefault());
                }).ToImmutableArray();

                var paramTypes = m.Parameters
                    .Where(static parameter => !IsCancellationTokenParameter(parameter))
                    .Select(static p => GetTypeName(p.Type))
                    .ToArray();
                var methodHash = Hashing.GetMethodHash(m.Name, paramTypes);

                var requestSchema = string.Join(";", paramArray
                    .Where(static parameter => !parameter.IsCancellationToken)
                    .Select(static parameter =>
                        $"{parameter.Name}:{parameter.Type}:{(parameter.IsStream ? "stream" : "value")}:{(parameter.PayloadNullable ? "nullable" : "required")}"));
                var responsePayload = isGenericTask
                    ? ((INamedTypeSymbol)m.ReturnType).TypeArguments[0]
                    : itemTypeSymbol;
                var responseNullable = responsePayload is not null && IsNullablePayload(responsePayload);
                var responseSchema = isStreamReturn
                    ? $"stream:{streamItemType}"
                    : $"value:{returnType}";
                if (responseNullable)
                    responseSchema += ":nullable";
                var kind = isOneWay ? "OneWay" : isStreamReturn
                    ? (paramArray.Any(static parameter => parameter.IsStream) ? "DuplexStreaming" : "ServerStreaming")
                    : paramArray.Any(static parameter => parameter.IsStream) ? "ClientStreaming" : "Unary";
                var canonical = $"{m.Name}|{methodHash}|{kind}|{requestSchema}|{responseSchema}|cancel={paramArray.Any(static parameter => parameter.IsCancellationToken)}|timeout={hasTimeoutAttribute}:{timeoutTicks?.ToString(CultureInfo.InvariantCulture)}|idempotent={isIdempotent}";

                return new RpcMethodModel(
                    Name: m.Name,
                    ReturnType: returnType,
                    DisplayReturnType: displayReturnType,
                    IsGenericTask: isGenericTask,
                    IsStreamReturn: isStreamReturn,
                    StreamItemType: streamItemType,
                    DisplayStreamItemType: displayStreamItemType,
                    GenericArgumentType: genericArg,
                    DisplayGenericArgumentType: displayGenericArg,
                    IsVoid: m.ReturnsVoid || isNonGenericTaskLike,
                    IsOneWay: isOneWay,
                    HasCancellationToken: paramArray.Any(p => p.IsCancellationToken),
                    HasTimeoutAttribute: hasTimeoutAttribute,
                    TimeoutTicks: timeoutTicks,
                    IsIdempotent: isIdempotent,
                    Hash: methodHash,
                    Parameters: paramArray,
                    RequestSchema: requestSchema,
                    ResponseSchema: responseSchema,
                    Fingerprint: Hashing.GetSha256(canonical),
                    ResponseNullable: responseNullable,
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
            GetGeneratedContractName(symbol),
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
}
