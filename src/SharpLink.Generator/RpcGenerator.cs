namespace SharpLink.Generator;

[Generator]
public class RpcGenerator : IIncrementalGenerator
{
    private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;
    private static readonly DiagnosticDescriptor InvalidReturnTypeRule = new(
        id: "SHARPLINK001",
        title: "Invalid RPC Return Type",
        messageFormat: "RPC method '{0}' must return Task/Task<T>/ValueTask/ValueTask<T>/IAsyncEnumerable<T>, but returns '{1}'",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
    private static readonly DiagnosticDescriptor MultipleCancellationTokensRule = new(
        id: "SHARPLINK002",
        title: "Invalid RPC CancellationToken Signature",
        messageFormat: "RPC method '{0}' can declare at most one CancellationToken parameter",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
    private static readonly DiagnosticDescriptor StreamParameterCountRule = new(
        id: "SHARPLINK003",
        title: "Invalid RPC Stream Parameter Count",
        messageFormat: "RPC method '{0}' defines {1} stream parameters, but at most 127 are supported",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
    private static readonly DiagnosticDescriptor TimeoutRequiresCancellationTokenRule = new(
        id: "SHARPLINK004",
        title: "Timeout Attribute Requires CancellationToken",
        messageFormat: "RPC method '{0}' uses [Timeout] but does not declare a CancellationToken parameter",
        category: "SharpLink.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var interfaces = context.SyntaxProvider
            .CreateSyntaxProvider(predicate: IsInterfaceCandidate, transform: GetInterfaceModelOrNull)
            .Where(m => m != null);

        var services = context.SyntaxProvider
            .CreateSyntaxProvider(predicate: IsClassCandidate, transform: GetServiceModelOrNull)
            .Where(m => m != null);

        var invalidMethods = context.SyntaxProvider
            .CreateSyntaxProvider(predicate: IsInterfaceCandidate, transform: GetInvalidRpcMethods)
            .Where(x => x.Length > 0);
        var invalidCancellationTokenMethods = context.SyntaxProvider
            .CreateSyntaxProvider(predicate: IsInterfaceCandidate, transform: GetInvalidCancellationTokenMethods)
            .Where(x => x.Length > 0);
        var invalidStreamCountMethods = context.SyntaxProvider
            .CreateSyntaxProvider(predicate: IsInterfaceCandidate, transform: GetInvalidStreamCountMethods)
            .Where(x => x.Length > 0);
        var invalidTimeoutCancellationMethods = context.SyntaxProvider
            .CreateSyntaxProvider(predicate: IsInterfaceCandidate, transform: GetInvalidTimeoutCancellationMethods)
            .Where(x => x.Length > 0);

        context.RegisterSourceOutput(invalidMethods, static (spc, methods) =>
        {
            foreach (var method in methods)
            {
                var diagnostic = Diagnostic.Create(
                    InvalidReturnTypeRule,
                    method.Location,
                    method.MethodName,
                    method.ReturnType);
                spc.ReportDiagnostic(diagnostic);
            }
        });
        context.RegisterSourceOutput(invalidCancellationTokenMethods, static (spc, methods) =>
        {
            foreach (var method in methods)
            {
                var diagnostic = Diagnostic.Create(
                    MultipleCancellationTokensRule,
                    method.Location,
                    method.MethodName);
                spc.ReportDiagnostic(diagnostic);
            }
        });
        context.RegisterSourceOutput(invalidStreamCountMethods, static (spc, methods) =>
        {
            foreach (var method in methods)
            {
                var diagnostic = Diagnostic.Create(
                    StreamParameterCountRule,
                    method.Location,
                    method.MethodName,
                    method.StreamParameterCount);
                spc.ReportDiagnostic(diagnostic);
            }
        });
        context.RegisterSourceOutput(invalidTimeoutCancellationMethods, static (spc, methods) =>
        {
            foreach (var method in methods)
            {
                var diagnostic = Diagnostic.Create(
                    TimeoutRequiresCancellationTokenRule,
                    method.Location,
                    method.MethodName);
                spc.ReportDiagnostic(diagnostic);
            }
        });

        context.RegisterSourceOutput(services, (spc, model) =>
        {
            var code = GenerateStub(model!);
            spc.AddSource($"{model!.ServiceName}_Stub.g.cs", SourceText.From(code, Encoding.UTF8));
        });

        context.RegisterSourceOutput(interfaces, (spc, model) =>
        {
            var code = GenerateProxy(model!);
            spc.AddSource($"{model!.Name}_Proxy.g.cs", SourceText.From(code, Encoding.UTF8));
        });
    }

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

    private static string GenerateProxy(RpcInterfaceModel model)
    {
        var nsDeclaration = string.IsNullOrEmpty(model.Namespace) ? "" : $"namespace {model.Namespace};";

        var sb = new StringBuilder();
        sb.AppendLine($$"""
                        // <auto-generated/>
                        #nullable enable
                        using SharpLink.Abstractions;
                        using SharpLink.Runtime;
                        using System;
                        using System.Buffers;
                        using System.Buffers.Binary;
                        using System.Collections.Generic;
                        using System.Runtime.CompilerServices;
                        using System.Threading;
                        using System.Threading.Tasks;

                        {{nsDeclaration}}

                        public class {{model.Name}}_Proxy(IRpcChannel channel, ISerializer serializer) : {{model.FullName}}
                        {
                            const long _interfaceHash = {{model.Hash}}L;
                        """);

        AppendSizeFieldsByType(sb, model.Methods);

        foreach (var method in model.Methods)
        {
            var paramList = string.Join(", ", method.Parameters.Select(p => $"{p.Type} {p.Name}"));
            var streamParams = method.Parameters.Where(p => p.IsStream).ToList();
            var payloadParams = method.Parameters.Where(p => !p.IsStream && !p.IsCancellationToken).ToList();
            var cancellationTokenParam = method.Parameters.FirstOrDefault(p => p.IsCancellationToken);
            var cancellationTokenArg = cancellationTokenParam is null ? "default" : cancellationTokenParam.Name;
            var blittablePayloadParams = payloadParams.Where(p => p.IsBlittable).ToList();
            var complexPayloadParams = payloadParams.Where(p => !p.IsBlittable).ToList();

            sb.AppendLine($"    public {(method.IsStreamReturn ? method.ReturnType : $"async {method.ReturnType}")} {method.Name}({paramList})");
            sb.AppendLine("    {");
            if (payloadParams.Count > 0)
            {
                sb.AppendLine("        Action<IBufferWriter<byte>> payloadWriter = (ibw) =>");
                sb.AppendLine("        {");
                sb.AppendLine("            var writer = (ArrayBufferWriter<byte>)ibw;");

                if (blittablePayloadParams.Count > 0)
                {
                    var totalExpr = string.Join(" + ", blittablePayloadParams.Select(p => GetSizeToken(p.Type)));
                    sb.AppendLine($"            var fixedSize = {totalExpr};");
                    sb.AppendLine("            var fixedSpan = writer.GetSpan(fixedSize);");
                    sb.AppendLine("            var fixedOffset = 0;");
                    foreach (var p in blittablePayloadParams)
                    {
                        var sizeToken = GetSizeToken(p.Type);
                        sb.AppendLine($"            System.Runtime.CompilerServices.Unsafe.WriteUnaligned(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(fixedSpan.Slice(fixedOffset, {sizeToken})), {p.Name});");
                        sb.AppendLine($"            fixedOffset += {sizeToken};");
                    }
                    sb.AppendLine("            writer.Advance(fixedSize);");
                }

                foreach (var p in complexPayloadParams)
                {
                    sb.AppendLine("            int lenOffset = writer.WrittenCount;");
                    sb.AppendLine("            writer.Advance(4);");
                    sb.AppendLine("            int start = writer.WrittenCount;");
                    sb.AppendLine($"            serializer.Serialize({p.Name}, writer);");
                    sb.AppendLine("            int len = writer.WrittenCount - start;");
                    sb.AppendLine("            var span = System.Runtime.InteropServices.MemoryMarshal.AsMemory(writer.WrittenMemory).Span;");
                    sb.AppendLine("            var lengthSlice = span.Slice(lenOffset, 4);");
                    sb.AppendLine("            BinaryPrimitives.WriteInt32LittleEndian(lengthSlice, len);");
                }

                sb.AppendLine("        };");
            }

            var hasPayload = payloadParams.Count > 0;
            var hasStream = streamParams.Count > 0;
            var timeoutArg = method.TimeoutSeconds is { } seconds
                ? $", TimeSpan.FromSeconds({seconds.ToString("R", InvariantCulture)}d)"
                : "";
            var hasTimeout = method.HasTimeoutAttribute;
            var hasTimeoutOverride = method.TimeoutSeconds is not null;

            string SelectByTimeout(string noTimeoutCall, string defaultTimeoutCall, string explicitTimeoutCall)
            {
                if (!hasTimeout)
                    return noTimeoutCall;
                return hasTimeoutOverride ? explicitTimeoutCall : defaultTimeoutCall;
            }

            if (hasStream)
            {
                sb.AppendLine("        Func<long, CancellationToken, Task> streamSender = async (requestId, cancellationToken) =>");
                sb.AppendLine("        {");
                if (streamParams.Count == 1)
                {
                    sb.AppendLine($"            await channel.SendClientStreamAsync(requestId, (sbyte)1, {streamParams[0].Name}, cancellationToken);");
                }
                else if (streamParams.Count == 2)
                {
                    sb.AppendLine($"            var task1 = channel.SendClientStreamAsync(requestId, (sbyte)1, {streamParams[0].Name}, cancellationToken);");
                    sb.AppendLine($"            var task2 = channel.SendClientStreamAsync(requestId, (sbyte)2, {streamParams[1].Name}, cancellationToken);");
                    sb.AppendLine("            await Task.WhenAll(task1, task2);");
                }
                else if (streamParams.Count == 3)
                {
                    sb.AppendLine($"            var task1 = channel.SendClientStreamAsync(requestId, (sbyte)1, {streamParams[0].Name}, cancellationToken);");
                    sb.AppendLine($"            var task2 = channel.SendClientStreamAsync(requestId, (sbyte)2, {streamParams[1].Name}, cancellationToken);");
                    sb.AppendLine($"            var task3 = channel.SendClientStreamAsync(requestId, (sbyte)3, {streamParams[2].Name}, cancellationToken);");
                    sb.AppendLine("            await Task.WhenAll(task1, task2, task3);");
                }
                else
                {
                    var streamId = 1;
                    foreach (var streamParam in streamParams)
                    {
                        sb.AppendLine($"            await channel.SendClientStreamAsync(requestId, (sbyte){streamId}, {streamParam.Name}, cancellationToken);");
                        streamId++;
                    }
                }
                sb.AppendLine("        };");
            }

            if (method.IsStreamReturn)
            {
                if (hasStream)
                {
                    sb.AppendLine(hasPayload
                        ? method.HasCancellationToken
                            ? SelectByTimeout(
                                $"        return channel.InvokeCancellableDuplexStreamAsync<{method.StreamItemType}>(_interfaceHash, {method.Hash}L, payloadWriter, streamSender, {cancellationTokenArg});",
                                $"        return channel.InvokeCancellableDuplexStreamWithDefaultTimeoutAsync<{method.StreamItemType}>(_interfaceHash, {method.Hash}L, payloadWriter, streamSender, {cancellationTokenArg});",
                                $"        return channel.InvokeCancellableDuplexStreamWithTimeoutAsync<{method.StreamItemType}>(_interfaceHash, {method.Hash}L, payloadWriter, streamSender{timeoutArg}, {cancellationTokenArg});")
                            : SelectByTimeout(
                                $"        return channel.InvokeDuplexStreamAsync<{method.StreamItemType}>(_interfaceHash, {method.Hash}L, payloadWriter, streamSender);",
                                $"        return channel.InvokeDuplexStreamWithDefaultTimeoutAsync<{method.StreamItemType}>(_interfaceHash, {method.Hash}L, payloadWriter, streamSender);",
                                $"        return channel.InvokeDuplexStreamWithTimeoutAsync<{method.StreamItemType}>(_interfaceHash, {method.Hash}L, payloadWriter, streamSender{timeoutArg});")
                        : method.HasCancellationToken
                            ? SelectByTimeout(
                                $"        return channel.InvokeCancellableDuplexStreamNoPayloadAsync<{method.StreamItemType}>(_interfaceHash, {method.Hash}L, streamSender, {cancellationTokenArg});",
                                $"        return channel.InvokeCancellableDuplexStreamWithDefaultTimeoutNoPayloadAsync<{method.StreamItemType}>(_interfaceHash, {method.Hash}L, streamSender, {cancellationTokenArg});",
                                $"        return channel.InvokeCancellableDuplexStreamWithTimeoutNoPayloadAsync<{method.StreamItemType}>(_interfaceHash, {method.Hash}L, streamSender{timeoutArg}, {cancellationTokenArg});")
                            : SelectByTimeout(
                                $"        return channel.InvokeDuplexStreamNoPayloadAsync<{method.StreamItemType}>(_interfaceHash, {method.Hash}L, streamSender);",
                                $"        return channel.InvokeDuplexStreamWithDefaultTimeoutNoPayloadAsync<{method.StreamItemType}>(_interfaceHash, {method.Hash}L, streamSender);",
                                $"        return channel.InvokeDuplexStreamWithTimeoutNoPayloadAsync<{method.StreamItemType}>(_interfaceHash, {method.Hash}L, streamSender{timeoutArg});"));
                }
                else
                {
                    sb.AppendLine(hasPayload
                        ? method.HasCancellationToken
                            ? SelectByTimeout(
                                $"        return channel.InvokeCancellableServerStreamAsync<{method.StreamItemType}>(_interfaceHash, {method.Hash}L, payloadWriter, {cancellationTokenArg});",
                                $"        return channel.InvokeCancellableServerStreamWithDefaultTimeoutAsync<{method.StreamItemType}>(_interfaceHash, {method.Hash}L, payloadWriter, {cancellationTokenArg});",
                                $"        return channel.InvokeCancellableServerStreamWithTimeoutAsync<{method.StreamItemType}>(_interfaceHash, {method.Hash}L, payloadWriter{timeoutArg}, {cancellationTokenArg});")
                            : SelectByTimeout(
                                $"        return channel.InvokeServerStreamAsync<{method.StreamItemType}>(_interfaceHash, {method.Hash}L, payloadWriter);",
                                $"        return channel.InvokeServerStreamWithDefaultTimeoutAsync<{method.StreamItemType}>(_interfaceHash, {method.Hash}L, payloadWriter);",
                                $"        return channel.InvokeServerStreamWithTimeoutAsync<{method.StreamItemType}>(_interfaceHash, {method.Hash}L, payloadWriter{timeoutArg});")
                        : method.HasCancellationToken
                            ? SelectByTimeout(
                                $"        return channel.InvokeCancellableServerStreamNoPayloadAsync<{method.StreamItemType}>(_interfaceHash, {method.Hash}L, {cancellationTokenArg});",
                                $"        return channel.InvokeCancellableServerStreamWithDefaultTimeoutNoPayloadAsync<{method.StreamItemType}>(_interfaceHash, {method.Hash}L, {cancellationTokenArg});",
                                $"        return channel.InvokeCancellableServerStreamWithTimeoutNoPayloadAsync<{method.StreamItemType}>(_interfaceHash, {method.Hash}L{timeoutArg}, {cancellationTokenArg});")
                            : SelectByTimeout(
                                $"        return channel.InvokeServerStreamNoPayloadAsync<{method.StreamItemType}>(_interfaceHash, {method.Hash}L);",
                                $"        return channel.InvokeServerStreamWithDefaultTimeoutNoPayloadAsync<{method.StreamItemType}>(_interfaceHash, {method.Hash}L);",
                                $"        return channel.InvokeServerStreamWithTimeoutNoPayloadAsync<{method.StreamItemType}>(_interfaceHash, {method.Hash}L{timeoutArg});"));
                }
            }
            else if (method.IsVoid)
            {
                if (method.IsOneWay)
                {
                    if (hasStream)
                    {
                        sb.AppendLine(hasPayload
                            ? method.HasCancellationToken
                                ? SelectByTimeout(
                                    $"        await channel.InvokeCancellableOneWayClientStreamAsync(_interfaceHash, {method.Hash}L, payloadWriter, streamSender, {cancellationTokenArg});",
                                    $"        await channel.InvokeCancellableOneWayClientStreamWithDefaultTimeoutAsync(_interfaceHash, {method.Hash}L, payloadWriter, streamSender, {cancellationTokenArg});",
                                    $"        await channel.InvokeCancellableOneWayClientStreamWithTimeoutAsync(_interfaceHash, {method.Hash}L, payloadWriter, streamSender{timeoutArg}, {cancellationTokenArg});")
                                : SelectByTimeout(
                                    $"        await channel.InvokeOneWayClientStreamAsync(_interfaceHash, {method.Hash}L, payloadWriter, streamSender);",
                                    $"        await channel.InvokeOneWayClientStreamWithDefaultTimeoutAsync(_interfaceHash, {method.Hash}L, payloadWriter, streamSender);",
                                    $"        await channel.InvokeOneWayClientStreamWithTimeoutAsync(_interfaceHash, {method.Hash}L, payloadWriter, streamSender{timeoutArg});")
                            : method.HasCancellationToken
                                ? SelectByTimeout(
                                    $"        await channel.InvokeCancellableOneWayClientStreamNoPayloadAsync(_interfaceHash, {method.Hash}L, streamSender, {cancellationTokenArg});",
                                    $"        await channel.InvokeCancellableOneWayClientStreamWithDefaultTimeoutNoPayloadAsync(_interfaceHash, {method.Hash}L, streamSender, {cancellationTokenArg});",
                                    $"        await channel.InvokeCancellableOneWayClientStreamWithTimeoutNoPayloadAsync(_interfaceHash, {method.Hash}L, streamSender{timeoutArg}, {cancellationTokenArg});")
                                : SelectByTimeout(
                                    $"        await channel.InvokeOneWayClientStreamNoPayloadAsync(_interfaceHash, {method.Hash}L, streamSender);",
                                    $"        await channel.InvokeOneWayClientStreamWithDefaultTimeoutNoPayloadAsync(_interfaceHash, {method.Hash}L, streamSender);",
                                    $"        await channel.InvokeOneWayClientStreamWithTimeoutNoPayloadAsync(_interfaceHash, {method.Hash}L, streamSender{timeoutArg});"));
                    }
                    else
                    {
                        sb.AppendLine(hasPayload
                            ? method.HasCancellationToken
                                ? SelectByTimeout(
                                    $"        await channel.InvokeCancellableOneWayAsync(_interfaceHash, {method.Hash}L, payloadWriter, {cancellationTokenArg});",
                                    $"        await channel.InvokeCancellableOneWayWithDefaultTimeoutAsync(_interfaceHash, {method.Hash}L, payloadWriter, {cancellationTokenArg});",
                                    $"        await channel.InvokeCancellableOneWayWithTimeoutAsync(_interfaceHash, {method.Hash}L, payloadWriter{timeoutArg}, {cancellationTokenArg});")
                                : SelectByTimeout(
                                    $"        await channel.InvokeOneWayAsync(_interfaceHash, {method.Hash}L, payloadWriter);",
                                    $"        await channel.InvokeOneWayWithDefaultTimeoutAsync(_interfaceHash, {method.Hash}L, payloadWriter);",
                                    $"        await channel.InvokeOneWayWithTimeoutAsync(_interfaceHash, {method.Hash}L, payloadWriter{timeoutArg});")
                            : method.HasCancellationToken
                                ? SelectByTimeout(
                                    $"        await channel.InvokeCancellableOneWayNoPayloadAsync(_interfaceHash, {method.Hash}L, {cancellationTokenArg});",
                                    $"        await channel.InvokeCancellableOneWayWithDefaultTimeoutNoPayloadAsync(_interfaceHash, {method.Hash}L, {cancellationTokenArg});",
                                    $"        await channel.InvokeCancellableOneWayWithTimeoutNoPayloadAsync(_interfaceHash, {method.Hash}L{timeoutArg}, {cancellationTokenArg});")
                                : SelectByTimeout(
                                    $"        await channel.InvokeOneWayNoPayloadAsync(_interfaceHash, {method.Hash}L);",
                                    $"        await channel.InvokeOneWayWithDefaultTimeoutNoPayloadAsync(_interfaceHash, {method.Hash}L);",
                                    $"        await channel.InvokeOneWayWithTimeoutNoPayloadAsync(_interfaceHash, {method.Hash}L{timeoutArg});"));
                    }
                }
                else
                {
                    if (hasStream)
                    {
                        sb.AppendLine(hasPayload
                            ? method.HasCancellationToken
                                ? SelectByTimeout(
                                    $"        await channel.InvokeCancellableClientStreamNoReturnAsync<byte>(_interfaceHash, {method.Hash}L, payloadWriter, streamSender, {cancellationTokenArg});",
                                    $"        await channel.InvokeCancellableClientStreamNoReturnWithDefaultTimeoutAsync<byte>(_interfaceHash, {method.Hash}L, payloadWriter, streamSender, {cancellationTokenArg});",
                                    $"        await channel.InvokeCancellableClientStreamNoReturnWithTimeoutAsync<byte>(_interfaceHash, {method.Hash}L, payloadWriter, streamSender{timeoutArg}, {cancellationTokenArg});")
                                : SelectByTimeout(
                                    $"        await channel.InvokeClientStreamNoReturnAsync<byte>(_interfaceHash, {method.Hash}L, payloadWriter, streamSender);",
                                    $"        await channel.InvokeClientStreamNoReturnWithDefaultTimeoutAsync<byte>(_interfaceHash, {method.Hash}L, payloadWriter, streamSender);",
                                    $"        await channel.InvokeClientStreamNoReturnWithTimeoutAsync<byte>(_interfaceHash, {method.Hash}L, payloadWriter, streamSender{timeoutArg});")
                            : method.HasCancellationToken
                                ? SelectByTimeout(
                                    $"        await channel.InvokeCancellableClientStreamNoReturnNoPayloadAsync<byte>(_interfaceHash, {method.Hash}L, streamSender, {cancellationTokenArg});",
                                    $"        await channel.InvokeCancellableClientStreamNoReturnWithDefaultTimeoutNoPayloadAsync<byte>(_interfaceHash, {method.Hash}L, streamSender, {cancellationTokenArg});",
                                    $"        await channel.InvokeCancellableClientStreamNoReturnWithTimeoutNoPayloadAsync<byte>(_interfaceHash, {method.Hash}L, streamSender{timeoutArg}, {cancellationTokenArg});")
                                : SelectByTimeout(
                                    $"        await channel.InvokeClientStreamNoReturnNoPayloadAsync<byte>(_interfaceHash, {method.Hash}L, streamSender);",
                                    $"        await channel.InvokeClientStreamNoReturnWithDefaultTimeoutNoPayloadAsync<byte>(_interfaceHash, {method.Hash}L, streamSender);",
                                    $"        await channel.InvokeClientStreamNoReturnWithTimeoutNoPayloadAsync<byte>(_interfaceHash, {method.Hash}L, streamSender{timeoutArg});"));
                    }
                    else
                    {
                        sb.AppendLine(hasPayload
                            ? method.HasCancellationToken
                                ? SelectByTimeout(
                                    $"        await channel.InvokeCancellableNoReturnAsync<byte>(_interfaceHash, {method.Hash}L, payloadWriter, {cancellationTokenArg});",
                                    $"        await channel.InvokeCancellableNoReturnWithDefaultTimeoutAsync<byte>(_interfaceHash, {method.Hash}L, payloadWriter, {cancellationTokenArg});",
                                    $"        await channel.InvokeCancellableNoReturnWithTimeoutAsync<byte>(_interfaceHash, {method.Hash}L, payloadWriter{timeoutArg}, {cancellationTokenArg});")
                                : SelectByTimeout(
                                    $"        await channel.InvokeNoReturnAsync<byte>(_interfaceHash, {method.Hash}L, payloadWriter);",
                                    $"        await channel.InvokeNoReturnWithDefaultTimeoutAsync<byte>(_interfaceHash, {method.Hash}L, payloadWriter);",
                                    $"        await channel.InvokeNoReturnWithTimeoutAsync<byte>(_interfaceHash, {method.Hash}L, payloadWriter{timeoutArg});")
                            : method.HasCancellationToken
                                ? SelectByTimeout(
                                    $"        await channel.InvokeCancellableNoReturnNoPayloadAsync<byte>(_interfaceHash, {method.Hash}L, {cancellationTokenArg});",
                                    $"        await channel.InvokeCancellableNoReturnWithDefaultTimeoutNoPayloadAsync<byte>(_interfaceHash, {method.Hash}L, {cancellationTokenArg});",
                                    $"        await channel.InvokeCancellableNoReturnWithTimeoutNoPayloadAsync<byte>(_interfaceHash, {method.Hash}L{timeoutArg}, {cancellationTokenArg});")
                                : SelectByTimeout(
                                    $"        await channel.InvokeNoReturnNoPayloadAsync<byte>(_interfaceHash, {method.Hash}L);",
                                    $"        await channel.InvokeNoReturnWithDefaultTimeoutNoPayloadAsync<byte>(_interfaceHash, {method.Hash}L);",
                                    $"        await channel.InvokeNoReturnWithTimeoutNoPayloadAsync<byte>(_interfaceHash, {method.Hash}L{timeoutArg});"));
                    }
                }
            }
            else
            {
                if (hasStream)
                {
                    sb.AppendLine(hasPayload
                        ? method.HasCancellationToken
                            ? SelectByTimeout(
                                $"        return await channel.InvokeCancellableClientStreamAsync<{method.GenericArgumentType}>(_interfaceHash, {method.Hash}L, payloadWriter, streamSender, {cancellationTokenArg});",
                                $"        return await channel.InvokeCancellableClientStreamWithDefaultTimeoutAsync<{method.GenericArgumentType}>(_interfaceHash, {method.Hash}L, payloadWriter, streamSender, {cancellationTokenArg});",
                                $"        return await channel.InvokeCancellableClientStreamWithTimeoutAsync<{method.GenericArgumentType}>(_interfaceHash, {method.Hash}L, payloadWriter, streamSender{timeoutArg}, {cancellationTokenArg});")
                            : SelectByTimeout(
                                $"        return await channel.InvokeClientStreamAsync<{method.GenericArgumentType}>(_interfaceHash, {method.Hash}L, payloadWriter, streamSender);",
                                $"        return await channel.InvokeClientStreamWithDefaultTimeoutAsync<{method.GenericArgumentType}>(_interfaceHash, {method.Hash}L, payloadWriter, streamSender);",
                                $"        return await channel.InvokeClientStreamWithTimeoutAsync<{method.GenericArgumentType}>(_interfaceHash, {method.Hash}L, payloadWriter, streamSender{timeoutArg});")
                        : method.HasCancellationToken
                            ? SelectByTimeout(
                                $"        return await channel.InvokeCancellableClientStreamNoPayloadAsync<{method.GenericArgumentType}>(_interfaceHash, {method.Hash}L, streamSender, {cancellationTokenArg});",
                                $"        return await channel.InvokeCancellableClientStreamWithDefaultTimeoutNoPayloadAsync<{method.GenericArgumentType}>(_interfaceHash, {method.Hash}L, streamSender, {cancellationTokenArg});",
                                $"        return await channel.InvokeCancellableClientStreamWithTimeoutNoPayloadAsync<{method.GenericArgumentType}>(_interfaceHash, {method.Hash}L, streamSender{timeoutArg}, {cancellationTokenArg});")
                            : SelectByTimeout(
                                $"        return await channel.InvokeClientStreamNoPayloadAsync<{method.GenericArgumentType}>(_interfaceHash, {method.Hash}L, streamSender);",
                                $"        return await channel.InvokeClientStreamWithDefaultTimeoutNoPayloadAsync<{method.GenericArgumentType}>(_interfaceHash, {method.Hash}L, streamSender);",
                                $"        return await channel.InvokeClientStreamWithTimeoutNoPayloadAsync<{method.GenericArgumentType}>(_interfaceHash, {method.Hash}L, streamSender{timeoutArg});"));
                }
                else
                {
                    sb.AppendLine(hasPayload
                        ? method.HasCancellationToken
                            ? SelectByTimeout(
                                $"        return await channel.InvokeCancellableAsync<{method.GenericArgumentType}>(_interfaceHash, {method.Hash}L, payloadWriter, {cancellationTokenArg});",
                                $"        return await channel.InvokeCancellableWithDefaultTimeoutAsync<{method.GenericArgumentType}>(_interfaceHash, {method.Hash}L, payloadWriter, {cancellationTokenArg});",
                                $"        return await channel.InvokeCancellableWithTimeoutAsync<{method.GenericArgumentType}>(_interfaceHash, {method.Hash}L, payloadWriter{timeoutArg}, {cancellationTokenArg});")
                            : SelectByTimeout(
                                $"        return await channel.InvokeAsync<{method.GenericArgumentType}>(_interfaceHash, {method.Hash}L, payloadWriter);",
                                $"        return await channel.InvokeWithDefaultTimeoutAsync<{method.GenericArgumentType}>(_interfaceHash, {method.Hash}L, payloadWriter);",
                                $"        return await channel.InvokeWithTimeoutAsync<{method.GenericArgumentType}>(_interfaceHash, {method.Hash}L, payloadWriter{timeoutArg});")
                        : method.HasCancellationToken
                            ? SelectByTimeout(
                                $"        return await channel.InvokeCancellableNoPayloadAsync<{method.GenericArgumentType}>(_interfaceHash, {method.Hash}L, {cancellationTokenArg});",
                                $"        return await channel.InvokeCancellableWithDefaultTimeoutNoPayloadAsync<{method.GenericArgumentType}>(_interfaceHash, {method.Hash}L, {cancellationTokenArg});",
                                $"        return await channel.InvokeCancellableWithTimeoutNoPayloadAsync<{method.GenericArgumentType}>(_interfaceHash, {method.Hash}L{timeoutArg}, {cancellationTokenArg});")
                            : SelectByTimeout(
                                $"        return await channel.InvokeNoPayloadAsync<{method.GenericArgumentType}>(_interfaceHash, {method.Hash}L);",
                                $"        return await channel.InvokeWithDefaultTimeoutNoPayloadAsync<{method.GenericArgumentType}>(_interfaceHash, {method.Hash}L);",
                                $"        return await channel.InvokeWithTimeoutNoPayloadAsync<{method.GenericArgumentType}>(_interfaceHash, {method.Hash}L{timeoutArg});"));
                }
            }

            sb.AppendLine("    }");
        }

        sb.AppendLine("}");
        sb.AppendLine($$"""

                        internal static class {{model.Name}}_ProxyRegistration
                        {
                            [ModuleInitializer]
                            internal static void Register()
                            {
                                SharpLink.Abstractions.GeneratedProxyRegistry.Register(typeof({{model.FullName}}), (channel, serializer) => new {{model.Name}}_Proxy(channel, serializer));
                            }
                        }
                        """);
        return sb.ToString();
    }

    private static string GenerateStub(RpcServiceModel model)
    {
        var nsDeclaration = string.IsNullOrEmpty(model.ServiceNamespace) ? "" : $"namespace {model.ServiceNamespace};";
        var noReturnMethods = model.Interface.Methods.Where(m => m.IsVoid || m.IsStreamReturn).ToArray();
        var responseMethods = model.Interface.Methods.Where(m => !m.IsVoid && !m.IsStreamReturn).ToArray();

        var sb = new StringBuilder();
        sb.AppendLine($$"""
                        // <auto-generated/>
                        #nullable enable
                        using SharpLink.Abstractions;
                        using System;
                        using System.Buffers;
                        using System.Collections.Generic;
                        using System.IO;
                        using System.Runtime.CompilerServices;
                        using System.Threading;
                        using System.Threading.Tasks;

                        {{nsDeclaration}}

                        public class {{model.ServiceName}}_Stub : IRpcStub
                        {
                            public long InterfaceHash => {{model.Interface.Hash}}L;
                        """);
        AppendSizeFieldsByType(sb, model.Interface.Methods);
        sb.AppendLine($$"""
                            private static async ValueTask __AwaitTaskResultAsync<T>(Task<T> task, IRpcSession session, IBufferWriter<byte> output)
                            {
                                var result = await task.ConfigureAwait(false);
                                session.Serializer.Serialize(result, output);
                            }

                            private static async ValueTask __AwaitValueTaskResultAsync<T>(ValueTask<T> task, IRpcSession session, IBufferWriter<byte> output)
                            {
                                var result = await task.ConfigureAwait(false);
                                session.Serializer.Serialize(result, output);
                            }

                            private static async ValueTask __AwaitTaskIgnoreAsync<T>(Task<T> task)
                            {
                                _ = await task.ConfigureAwait(false);
                            }

                            private static async ValueTask __AwaitValueTaskIgnoreAsync<T>(ValueTask<T> task)
                            {
                                _ = await task.ConfigureAwait(false);
                            }

                            private static async ValueTask __PumpStreamAsync<T>(IAsyncEnumerable<T> stream, IRpcSession session, long requestId)
                            {
                                try
                                {
                                    await foreach (var item in stream.ConfigureAwait(false))
                                        SharpLink.Runtime.RpcSessionExtensions.SendStreamChunkAsync(session, requestId, 0, item);

                                    SharpLink.Runtime.RpcSessionExtensions.SendStreamCompleteAsync(session, requestId, 0);
                                }
                                catch (Exception ex)
                                {
                                    SharpLink.Runtime.RpcSessionExtensions.SendStreamErrorAsync(session, requestId, 0, ex.Message);
                                }
                            }

                            public ValueTask InvokeNoReturnAsync(object service, IRpcSession session, long methodHash, long requestId, ReadOnlySequence<byte> args)
                                => InvokeNoReturnCoreAsync(service, session, methodHash, requestId, args, CancellationToken.None);

                            public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcSession session, long methodHash, long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken)
                                => InvokeNoReturnCoreAsync(service, session, methodHash, requestId, args, cancellationToken);

                            private ValueTask InvokeNoReturnCoreAsync(object service, IRpcSession session, long methodHash, long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken)
                            {
                                var impl = ({{model.Interface.FullName}})service;
                                var reader = new SequenceReader<byte>(args);
                        """);

        if (noReturnMethods.Length == 0)
        {
            sb.AppendLine("""
                                throw new RpcException("Method not found");
                            }
""");
        }
        else
        {
            sb.AppendLine("""
                                switch (methodHash)
                                {
""");
            AppendStubDispatchCases(sb, noReturnMethods, writeResponse: false);
            sb.AppendLine("""
                                  default: throw new RpcException("Method not found");
                              }

                              return ValueTask.CompletedTask;
                          }
""");
        }

        sb.AppendLine($$"""
                            public ValueTask InvokeAsync(object service, IRpcSession session, long methodHash, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output)
                                => InvokeCoreAsync(service, session, methodHash, requestId, args, output, CancellationToken.None);

                            public ValueTask InvokeCancellableAsync(object service, IRpcSession session, long methodHash, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output, CancellationToken cancellationToken)
                                => InvokeCoreAsync(service, session, methodHash, requestId, args, output, cancellationToken);

                            private ValueTask InvokeCoreAsync(object service, IRpcSession session, long methodHash, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output, CancellationToken cancellationToken)
                            {
                                var impl = ({{model.Interface.FullName}})service;
                                var reader = new SequenceReader<byte>(args);
                        """);

        if (responseMethods.Length == 0)
        {
            sb.AppendLine("""
                                throw new RpcException("Method not found");
                            }
""");
        }
        else
        {
            sb.AppendLine("""
                                switch (methodHash)
                                {
""");
            AppendStubDispatchCases(sb, responseMethods, writeResponse: true);
            sb.AppendLine("""
                                  default: throw new RpcException("Method not found");
                              }

                              return ValueTask.CompletedTask;
                          }
""");
        }

        sb.AppendLine("""

                          internal static class __SharpLinkStubRegistration
                          {
                              [ModuleInitializer]
                              internal static void Register()
                              {
                                  SharpLink.Abstractions.GeneratedStubRegistry.Register(typeof(__SERVICE_TYPE__), () => new __STUB_TYPE__());
                              }
                          }
                      }
                      """);
        return sb.ToString()
            .Replace("__SERVICE_TYPE__", model.ServiceFullName)
            .Replace("__STUB_TYPE__", $"{model.ServiceName}_Stub");
    }

    private static void AppendStubDispatchCases(StringBuilder sb, IEnumerable<RpcMethodModel> methods, bool writeResponse)
    {
        foreach (var method in methods)
        {
            sb.AppendLine($"            case {method.Hash}L:");
            sb.AppendLine("            {");
            var streamParams = method.Parameters.Where(p => p.IsStream).ToList();
            var blittableParams = method.Parameters.Where(p => !p.IsStream && p is { IsCancellationToken: false, IsBlittable: true }).ToList();
            var complexParams = method.Parameters.Where(p => !p.IsStream && p is { IsCancellationToken: false, IsBlittable: false }).ToList();
            var streamId = 1;

            foreach (var p in streamParams)
            {
                sb.AppendLine($"                var dispatcher_{p.Name} = SharpLink.Runtime.PooledAsyncStreamDispatcher<{p.StreamItemType}>.Rent(session.Serializer, cancellationToken);");
                sb.AppendLine($"                session.StreamManager.Register(requestId, (sbyte){streamId}, dispatcher_{p.Name});");
                streamId++;
            }

            foreach (var p in method.Parameters.Where(p => !p.IsStream))
            {
                sb.AppendLine($"                {p.Type} arg_{p.Name};");
            }

            foreach (var p in method.Parameters.Where(p => p.IsCancellationToken))
            {
                sb.AppendLine($"                arg_{p.Name} = cancellationToken;");
            }

            foreach (var p in blittableParams)
            {
                var sizeToken = GetSizeToken(p.Type);
                sb.AppendLine($"                if (reader.Remaining < {sizeToken}) throw new InvalidDataException();");
                sb.AppendLine($"                if (reader.UnreadSpan.Length >= {sizeToken})");
                sb.AppendLine("                {");
                sb.AppendLine($"                    arg_{p.Name} = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<{p.Type}>(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(reader.UnreadSpan));");
                sb.AppendLine("                }");
                sb.AppendLine("                else");
                sb.AppendLine("                {");
                sb.AppendLine($"                    byte[] tmp_{p.Name} = new byte[{sizeToken}];");
                sb.AppendLine($"                    if (!reader.TryCopyTo(tmp_{p.Name})) throw new InvalidDataException();");
                sb.AppendLine($"                    arg_{p.Name} = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<{p.Type}>(ref tmp_{p.Name}[0]);");
                sb.AppendLine("                }");
                sb.AppendLine($"                reader.Advance({sizeToken});");
            }

            foreach (var p in complexParams)
            {
                sb.AppendLine($"                if (!reader.TryReadLittleEndian(out int len_{p.Name})) throw new InvalidDataException();");
                sb.AppendLine($"                if (len_{p.Name} < 0 || reader.Remaining < len_{p.Name}) throw new InvalidDataException();");
                sb.AppendLine($"                var seq_{p.Name} = reader.UnreadSequence.Slice(0, len_{p.Name});");
                if (p.IsValueType)
                {
                    sb.AppendLine($"                arg_{p.Name} = session.Serializer.Deserialize<{p.Type}>(ref seq_{p.Name});");
                }
                else if (p.IsNullableReference)
                {
                    sb.AppendLine($"                arg_{p.Name} = session.Serializer.Deserialize<{p.Type}>(ref seq_{p.Name});");
                }
                else
                {
                    sb.AppendLine($"                arg_{p.Name} = session.Serializer.Deserialize<{p.Type}>(ref seq_{p.Name}) ?? throw new InvalidDataException(\"Argument {p.Name} is null.\");");
                }
                sb.AppendLine($"                reader.Advance(len_{p.Name});");
            }

            var callArgs = string.Join(", ", method.Parameters.Select(p => p.IsStream ? $"dispatcher_{p.Name}" : $"arg_{p.Name}"));
            var callLine = $"impl.{method.Name}({callArgs})";

            if (method.IsStreamReturn)
            {
                sb.AppendLine($"                var resultStream = {callLine};");
                sb.AppendLine("                return __PumpStreamAsync(resultStream, session, requestId);");
            }
            else if (method.IsVoid)
            {
                if (method.ReturnType.Contains("ValueTask"))
                {
                    sb.AppendLine($"                var pending = {callLine};");
                    sb.AppendLine("                if (!pending.IsCompletedSuccessfully)");
                    sb.AppendLine("                    return pending;");
                }
                else
                {
                    sb.AppendLine($"                var pending = {callLine};");
                    sb.AppendLine("                if (!pending.IsCompletedSuccessfully)");
                    sb.AppendLine("                    return new ValueTask(pending);");
                }
            }
            else
            {
                if (method.ReturnType.Contains("ValueTask"))
                {
                    sb.AppendLine($"                var pending = {callLine};");
                    sb.AppendLine("                if (pending.IsCompletedSuccessfully)");
                    sb.AppendLine("                {");
                    if (writeResponse)
                        sb.AppendLine("                    session.Serializer.Serialize(pending.Result, output);");
                    sb.AppendLine("                }");
                    sb.AppendLine("                else");
                    sb.AppendLine("                {");
                    if (writeResponse)
                        sb.AppendLine("                    return __AwaitValueTaskResultAsync(pending, session, output);");
                    else
                        sb.AppendLine("                    return __AwaitValueTaskIgnoreAsync(pending);");
                    sb.AppendLine("                }");
                }
                else
                {
                    sb.AppendLine($"                var pending = {callLine};");
                    sb.AppendLine("                if (pending.IsCompletedSuccessfully)");
                    sb.AppendLine("                {");
                    if (writeResponse)
                        sb.AppendLine("                    session.Serializer.Serialize(pending.GetAwaiter().GetResult(), output);");
                    sb.AppendLine("                }");
                    sb.AppendLine("                else");
                    sb.AppendLine("                {");
                    if (writeResponse)
                        sb.AppendLine("                    return __AwaitTaskResultAsync(pending, session, output);");
                    else
                        sb.AppendLine("                    return __AwaitTaskIgnoreAsync(pending);");
                    sb.AppendLine("                }");
                }
            }

            if (!method.IsStreamReturn)
                sb.AppendLine("                break;");
            sb.AppendLine("            }");
        }
    }

    private static void AppendSizeFieldsByType(StringBuilder sb, EquatableArray<RpcMethodModel> methods)
    {
        var blittableTypes = methods
            .SelectMany(m => m.Parameters)
            .Where(p => !p.IsStream && p is { IsCancellationToken: false, IsBlittable: true })
            .Select(p => p.Type)
            .Distinct()
            .ToArray();

        foreach (var type in blittableTypes)
        {
            if (TryGetConstantSize(type, out _))
                continue;

            var fieldName = GetSizeFieldNameByType(type);
            sb.AppendLine($"    private static readonly int {fieldName} = {GetSizeExpression(type)};");
        }
    }

    private static string GetSizeFieldNameByType(string typeName)
    {
        var sanitized = typeName
            .Replace("global::", "")
            .Replace(".", "_")
            .Replace("::", "_")
            .Replace("<", "_")
            .Replace(">", "_")
            .Replace(",", "_")
            .Replace(" ", "")
            .Replace("(", "_")
            .Replace(")", "_")
            .Replace("[", "_")
            .Replace("]", "_");

        return $"__size_type_{sanitized}";
    }

    private static string GetSizeToken(string typeName)
    {
        if (TryGetConstantSize(typeName, out var size))
            return size.ToString();

        return GetSizeFieldNameByType(typeName);
    }

    private static string GetSizeExpression(string typeName)
    {
        return $"System.Runtime.CompilerServices.Unsafe.SizeOf<{typeName}>()";
    }

    private static bool TryGetConstantSize(string typeName, out int size)
    {
        var t = typeName.Replace("global::", "");
        switch (t)
        {
            case "bool":
            case "System.Boolean":
            case "byte":
            case "System.Byte":
            case "sbyte":
            case "System.SByte":
                size = 1; return true;
            case "short":
            case "System.Int16":
            case "ushort":
            case "System.UInt16":
            case "char":
            case "System.Char":
                size = 2; return true;
            case "int":
            case "System.Int32":
            case "uint":
            case "System.UInt32":
            case "float":
            case "System.Single":
                size = 4; return true;
            case "long":
            case "System.Int64":
            case "ulong":
            case "System.UInt64":
            case "double":
            case "System.Double":
            case "System.DateTime":
            case "System.TimeSpan":
                size = 8; return true;
            case "decimal":
            case "System.Decimal":
            case "System.Guid":
                size = 16; return true;
            default:
                size = 0; return false;
        }
    }
}

internal record RpcServiceModel(string ServiceName, string ServiceNamespace, string ServiceFullName, RpcInterfaceModel Interface);

internal record RpcInterfaceModel(
    string Name,
    string Namespace,
    string FullName,
    long Hash,
    EquatableArray<RpcMethodModel> Methods);

internal record RpcMethodModel(
    string Name,
    string ReturnType,
    bool IsGenericTask,
    bool IsStreamReturn,
    string? StreamItemType,
    string? GenericArgumentType,
    bool IsVoid,
    bool IsOneWay,
    bool HasCancellationToken,
    bool HasTimeoutAttribute,
    double? TimeoutSeconds,
    long Hash,
    EquatableArray<RpcParameterModel> Parameters);

internal record RpcParameterModel(
    string Name,
    string Type,
    bool IsStream,
    string? StreamItemType,
    bool IsBlittable,
    bool IsValueType,
    bool IsNullableReference,
    bool IsCancellationToken);

internal readonly record struct InvalidRpcMethodModel(string MethodName, string ReturnType, Location? Location);
internal readonly record struct InvalidCancellationTokenMethodModel(string MethodName, Location? Location);
internal readonly record struct InvalidStreamCountMethodModel(string MethodName, int StreamParameterCount, Location? Location);
internal readonly record struct InvalidTimeoutCancellationMethodModel(string MethodName, Location? Location);

file static class Hashing
{
    private const ulong FnvPrime = 1099511628211;
    private const ulong FnvOffsetBasis = 14695981039346656037;

    public static long GetMethodHash(string mName, string[] pNames)
    {
        var cleanP = string.Join(",", pNames).Replace("global::", "").Replace(" ", "");
        return (long)Hash($"{mName}({cleanP})");
    }

    public static long GetInterfaceHash(string iName)
    {
        return (long)Hash(iName.Replace("global::", "").Replace(" ", ""));
    }

    private static ulong Hash(string s)
    {
        ulong hash = FnvOffsetBasis;
        foreach (var c in s)
        {
            hash ^= c;
            hash *= FnvPrime;
        }
        return hash;
    }
}
