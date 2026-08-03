namespace SharpLink.CodeFixes;

internal sealed partial class SharpLinkCodeFixProvider
{
    private static async Task<TNode?> FindNodeAsync<TNode>(
        Document document,
        Diagnostic diagnostic,
        CancellationToken cancellationToken)
        where TNode : SyntaxNode
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        return root?.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
            .AncestorsAndSelf().OfType<TNode>().FirstOrDefault();
    }

    private static async Task<Document> ReplaceNodeAsync(
        Document document,
        SyntaxNode oldNode,
        SyntaxNode newNode,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        return root is null ? document : document.WithSyntaxRoot(root.ReplaceNode(oldNode, newNode));
    }

    private static AttributeListSyntax CreateAttributeList(string attributeName)
        => SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(
            SyntaxFactory.Attribute(SyntaxFactory.ParseName(attributeName))));

    private static bool AttributeMatches(
        SemanticModel semanticModel,
        AttributeSyntax attribute,
        string metadataName,
        CancellationToken cancellationToken)
        => semanticModel.GetSymbolInfo(attribute, cancellationToken).Symbol is IMethodSymbol constructor &&
           string.Equals(constructor.ContainingType.ToDisplayString(), metadataName, StringComparison.Ordinal);

    private static bool HasAttribute(ISymbol symbol, string metadataName)
        => symbol.GetAttributes().Any(item =>
            string.Equals(item.AttributeClass?.ToDisplayString(), metadataName, StringComparison.Ordinal));

    private static bool IsTimeoutAttribute(AttributeData attribute)
        => string.Equals(
               attribute.AttributeClass?.ToDisplayString(),
               "SharpLink.Sdk.TimeoutAttribute",
               StringComparison.Ordinal) ||
           string.Equals(
               attribute.AttributeClass?.ToDisplayString(),
               "SharpLink.Abstractions.TimeoutAttribute",
               StringComparison.Ordinal);

    private static bool IsOnewayAttribute(AttributeData attribute)
        => string.Equals(
               attribute.AttributeClass?.ToDisplayString(),
               "SharpLink.Sdk.OnewayAttribute",
               StringComparison.Ordinal) ||
           string.Equals(
               attribute.AttributeClass?.ToDisplayString(),
               "SharpLink.Abstractions.OnewayAttribute",
               StringComparison.Ordinal);

    private static bool IsActivatorUtilitiesConstructorAttribute(AttributeData attribute)
        => string.Equals(
            attribute.AttributeClass?.ToDisplayString(),
            "Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute",
            StringComparison.Ordinal);

    private static bool IsRpcUnionCaseAttribute(AttributeData attribute)
        => string.Equals(
            attribute.AttributeClass?.ToDisplayString(),
            "SharpLink.Sdk.RpcUnionCaseAttribute",
            StringComparison.Ordinal);

    private static bool IsSerializableRpcMember(ISymbol member)
        => !member.IsStatic && member.DeclaredAccessibility == Accessibility.Public &&
           !HasAttribute(member, "SharpLink.Sdk.RpcIgnoreAttribute") &&
           (member is IFieldSymbol { IsConst: false } ||
            member is IPropertySymbol
            {
                IsIndexer: false,
                GetMethod.DeclaredAccessibility: Accessibility.Public
            });

    private static bool TryGetRpcMemberId(ISymbol member, out uint id)
    {
        var attribute = member.GetAttributes().FirstOrDefault(item => string.Equals(
            item.AttributeClass?.ToDisplayString(),
            "SharpLink.Sdk.RpcMemberAttribute",
            StringComparison.Ordinal));
        if (attribute is not null)
        {
            if (attribute.ConstructorArguments.Length == 1 &&
                attribute.ConstructorArguments[0].Value is int explicitId &&
                explicitId is > 0 and <= 0x1FFF_FFFF)
            {
                id = (uint)explicitId;
                return true;
            }
            id = 0;
            return false;
        }

        var hash = 2166136261U;
        foreach (var character in member.Name)
        {
            hash ^= character;
            hash *= 16777619U;
        }
        id = hash & 0x1FFF_FFFFU;
        if (id == 0)
            id = 1;
        return true;
    }

    private static bool HasNonCancellableAttribute(IMethodSymbol method)
        => method.GetAttributes().Any(IsNonCancellableAttribute);

    private static bool IsNonCancellableAttribute(AttributeData attribute)
        => string.Equals(
               attribute.AttributeClass?.ToDisplayString(),
               "SharpLink.Sdk.NonCancellableAttribute",
               StringComparison.Ordinal) ||
           string.Equals(
               attribute.AttributeClass?.ToDisplayString(),
               "SharpLink.Abstractions.NonCancellableAttribute",
               StringComparison.Ordinal);

    private static async Task<ImmutableArray<IMethodSymbol>> FindEquivalentInterfaceMethodsAsync(
        IMethodSymbol method,
        Solution solution,
        CancellationToken cancellationToken)
    {
        if (method.ContainingType.TypeKind != TypeKind.Interface)
            return ImmutableArray.Create(method);

        var contractTypes = new List<INamedTypeSymbol>();
        if (HasRpcContractAttribute(method.ContainingType))
            contractTypes.Add(method.ContainingType);

        var derivedInterfaces = await SymbolFinder.FindDerivedInterfacesAsync(
            method.ContainingType,
            solution,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var derived in derivedInterfaces.Where(HasRpcContractAttribute))
        {
            if (!contractTypes.Any(existing => SymbolEqualityComparer.Default.Equals(existing, derived)))
                contractTypes.Add(derived);
        }

        if (contractTypes.Count == 0)
            contractTypes.Add(method.ContainingType);

        var methods = new List<IMethodSymbol>();
        foreach (var contract in contractTypes)
        {
            foreach (var @interface in new[] { contract }.Concat(contract.AllInterfaces)
                         .Where(static candidate => !IsIService(candidate)))
            {
                foreach (var candidate in @interface.GetMembers(method.Name).OfType<IMethodSymbol>()
                             .Where(static candidate => candidate.MethodKind == MethodKind.Ordinary &&
                                                        candidate.DeclaredAccessibility == Accessibility.Public))
                {
                    if (!HasEquivalentContractSignature(method, candidate) ||
                        methods.Any(existing => SymbolEqualityComparer.Default.Equals(existing, candidate)))
                    {
                        continue;
                    }
                    methods.Add(candidate);
                }
            }
        }
        return methods.ToImmutableArray();
    }

    private static bool HasRpcContractAttribute(INamedTypeSymbol type)
        => HasAttribute(type, "SharpLink.Sdk.RpcContractAttribute") ||
           HasAttribute(type, "SharpLink.Abstractions.RpcContractAttribute");

    private static bool HasEquivalentContractSignature(IMethodSymbol left, IMethodSymbol right)
    {
        if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal) ||
            left.Arity != right.Arity || left.Parameters.Length != right.Parameters.Length)
        {
            return false;
        }
        for (var index = 0; index < left.Parameters.Length; index++)
        {
            if (left.Parameters[index].RefKind != right.Parameters[index].RefKind ||
                !SymbolEqualityComparer.Default.Equals(
                    left.Parameters[index].Type,
                    right.Parameters[index].Type))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsIService(INamedTypeSymbol type)
        => type.Name == "IService" && type.ContainingNamespace.ToDisplayString() == "SharpLink.Sdk";

    private static bool IsCodecAdapter(INamedTypeSymbol type)
        => type.Name == "IRpcCodecAdapter" &&
           type.ContainingNamespace.ToDisplayString() == "SharpLink.Abstractions";

    private static bool ContainsTypeParameter(ITypeSymbol type)
        => type.TypeKind == TypeKind.TypeParameter ||
           type is INamedTypeSymbol named &&
           (named.TypeArguments.Any(ContainsTypeParameter) ||
            named.ContainingType is not null && ContainsTypeParameter(named.ContainingType));

    private static bool IsEffectivelyPublic(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
                return false;
        }
        return true;
    }

    private static bool HasRegularEditableDeclaration(ISymbol symbol, Solution solution)
        => symbol.DeclaringSyntaxReferences.Any(reference =>
            IsRegularEditableDocument(solution, reference.SyntaxTree));

    private static bool HasOnlyRegularEditableDeclarations(ISymbol symbol, Solution solution)
        => symbol.DeclaringSyntaxReferences.Length != 0 &&
           symbol.DeclaringSyntaxReferences.All(reference =>
               IsRegularEditableDocument(solution, reference.SyntaxTree));

    private static bool HasDeclarationInProject(
        ISymbol symbol,
        Solution solution,
        ProjectId projectId)
        => symbol.DeclaringSyntaxReferences.Any(reference =>
            solution.GetDocument(reference.SyntaxTree)?.Project.Id == projectId);

    private static bool IsRegularEditableDocument(Solution solution, SyntaxTree syntaxTree)
    {
        var document = solution.GetDocument(syntaxTree);
        return document is not null &&
               document.Project.Documents.Any(candidate => candidate.Id == document.Id);
    }

    private static bool TryGetPublicizationClosure(
        INamedTypeSymbol root,
        Solution solution,
        out ImmutableArray<INamedTypeSymbol> types)
    {
        var result = new List<INamedTypeSymbol>();
        var pending = new Queue<INamedTypeSymbol>();
        Add(root.OriginalDefinition);

        while (pending.Count != 0)
        {
            var current = pending.Dequeue();
            if (HasFileLocalNameCollision(current) ||
                current.DeclaringSyntaxReferences.Length == 0 ||
                current.DeclaringSyntaxReferences.Any(reference =>
                    !IsRegularEditableDocument(solution, reference.SyntaxTree) ||
                    reference.GetSyntax() is not BaseTypeDeclarationSyntax))
            {
                types = default;
                return false;
            }
            if (current.ContainingType is { } containing)
                Add(containing.OriginalDefinition);
            if (current.BaseType is { } baseType && !AddAccessibilityDependency(baseType))
            {
                types = default;
                return false;
            }
            foreach (var @interface in current.Interfaces)
            {
                if (!AddAccessibilityDependency(@interface))
                {
                    types = default;
                    return false;
                }
            }
            foreach (var typeParameter in current.TypeParameters)
            {
                foreach (var constraint in typeParameter.ConstraintTypes)
                {
                    if (!AddAccessibilityDependency(constraint))
                    {
                        types = default;
                        return false;
                    }
                }
            }
            foreach (var member in current.GetMembers().Where(static member =>
                         member.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or
                             Accessibility.ProtectedOrInternal))
            {
                if (!AddMemberAccessibilityDependencies(member))
                {
                    types = default;
                    return false;
                }
            }
        }

        types = result.ToImmutableArray();
        return true;

        void Add(INamedTypeSymbol candidate)
        {
            if (result.Any(existing => SymbolEqualityComparer.Default.Equals(existing, candidate)))
                return;
            result.Add(candidate);
            pending.Enqueue(candidate);
        }

        bool AddAccessibilityDependency(ITypeSymbol dependency)
        {
            switch (dependency)
            {
                case IArrayTypeSymbol array:
                    return AddAccessibilityDependency(array.ElementType);
                case IPointerTypeSymbol pointer:
                    return AddAccessibilityDependency(pointer.PointedAtType);
                case IFunctionPointerTypeSymbol functionPointer:
                    return AddAccessibilityDependency(functionPointer.Signature.ReturnType) &&
                           functionPointer.Signature.Parameters.All(parameter =>
                               AddAccessibilityDependency(parameter.Type));
                case IErrorTypeSymbol:
                    return false;
                case INamedTypeSymbol named:
                    {
                        var definition = named.OriginalDefinition;
                        if (!IsEffectivelyPublic(definition))
                        {
                            if (definition.DeclaringSyntaxReferences.Length == 0 ||
                                definition.DeclaringSyntaxReferences.Any(static reference =>
                                    reference.GetSyntax() is not BaseTypeDeclarationSyntax))
                            {
                                return false;
                            }
                            Add(definition);
                        }
                        if (named.ContainingType is { } containingType &&
                            !AddAccessibilityDependency(containingType))
                        {
                            return false;
                        }
                        foreach (var argument in named.TypeArguments)
                        {
                            if (!AddAccessibilityDependency(argument))
                                return false;
                        }
                        return true;
                    }
                case ITypeParameterSymbol:
                case IDynamicTypeSymbol:
                    return true;
                default:
                    return true;
            }
        }

        bool AddMemberAccessibilityDependencies(ISymbol member)
        {
            switch (member)
            {
                case INamedTypeSymbol nestedType:
                    return AddAccessibilityDependency(nestedType);
                case IFieldSymbol field:
                    return AddAccessibilityDependency(field.Type);
                case IEventSymbol @event:
                    return AddAccessibilityDependency(@event.Type);
                case IPropertySymbol property:
                    return AddAccessibilityDependency(property.Type) &&
                           property.Parameters.All(parameter =>
                               AddAccessibilityDependency(parameter.Type));
                case IMethodSymbol method:
                    if (!AddAccessibilityDependency(method.ReturnType) ||
                        !method.Parameters.All(parameter =>
                            AddAccessibilityDependency(parameter.Type)))
                    {
                        return false;
                    }
                    return method.TypeParameters.All(typeParameter =>
                        typeParameter.ConstraintTypes.All(AddAccessibilityDependency));
                default:
                    return true;
            }
        }
    }

    private static bool HasFileLocalNameCollision(INamedTypeSymbol type)
    {
        var isFileLocal = type.DeclaringSyntaxReferences.Any(static reference =>
            reference.GetSyntax() is BaseTypeDeclarationSyntax declaration &&
            declaration.Modifiers.Any(SyntaxKind.FileKeyword));
        return isFileLocal && type.ContainingType is null &&
               type.ContainingNamespace.GetTypeMembers(type.Name, type.Arity).Any(candidate =>
                   !SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, type.OriginalDefinition));
    }
}
