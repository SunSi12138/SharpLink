using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

internal sealed class SymbolInventory(
    string project, string framework, string assembly, IReadOnlyDictionary<SyntaxTree, DocumentEvidence> documents)
{
    private static readonly SymbolDisplayFormat Display = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints | SymbolDisplayGenericsOptions.IncludeVariance,
        memberOptions: SymbolDisplayMemberOptions.IncludeAccessibility | SymbolDisplayMemberOptions.IncludeModifiers |
            SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeParameters |
            SymbolDisplayMemberOptions.IncludeExplicitInterface | SymbolDisplayMemberOptions.IncludeContainingType |
            SymbolDisplayMemberOptions.IncludeRef | SymbolDisplayMemberOptions.IncludeConstantValue,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName |
            SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeParamsRefOut |
            SymbolDisplayParameterOptions.IncludeExtensionThis,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);
    private readonly HashSet<string> ids = new(StringComparer.Ordinal);
    internal List<SymbolRow> Symbols { get; } = [];
    internal HashSet<Relation> Relations { get; } = [];

    internal void Collect(IAssemblySymbol source)
    {
        foreach (var attribute in source.GetAttributes())
            if (attribute.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.InternalsVisibleToAttribute")
            {
                var grant = attribute.ConstructorArguments.Single().Value?.ToString()
                    ?? throw new InvalidOperationException("IVT grant has no target assembly.");
                Relations.Add(new Relation(project + "@" + framework + "|" + assembly, "internals-visible-to", grant, grant, "AssemblyGrant"));
            }
        VisitNamespace(source.GlobalNamespace);
    }

    private void VisitNamespace(INamespaceSymbol scope)
    {
        foreach (var child in scope.GetNamespaceMembers())
            VisitNamespace(child);
        foreach (var type in scope.GetTypeMembers())
            Visit(type);
    }

    private void Visit(ISymbol symbol)
    {
        // Roslyn returns a partial definition as the canonical member. If an
        // implementation is also surfaced, its locations are merged into that row.
        if (symbol is IMethodSymbol { PartialDefinitionPart: not null } or IPropertySymbol { PartialDefinitionPart: not null } or IEventSymbol { PartialDefinitionPart: not null })
            return;
        if (symbol is not (INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol))
            throw new InvalidOperationException($"Unaccounted member kind {symbol.Kind}: {symbol.ToDisplayString()}");
        var id = Id(symbol);
        if (!ids.Add(id))
            throw new InvalidOperationException($"Duplicate stable symbol ID: {id}");
        var declarations = SyntaxReferences(symbol).Select(reference =>
        {
            if (!documents.TryGetValue(reference.SyntaxTree, out var document))
                throw new InvalidOperationException($"Declaration tree is absent from document inventory: {reference.SyntaxTree.FilePath}");
            var lineSpan = reference.SyntaxTree.GetLineSpan(reference.Span);
            return new SourceEvidence(document.Path, lineSpan.StartLinePosition.Line + 1, lineSpan.EndLinePosition.Line + 1,
                reference.Span.Start, reference.Span.Length, InventoryReport.Hash(reference.SyntaxTree.GetText().ToString(reference.Span)), document.Kind == "source-generated");
        }).Distinct().OrderBy(location => location.Path, StringComparer.Ordinal).ThenBy(location => location.Start).ToArray();
        // Implicit accessors/backing fields can have no syntax reference of their own.
        var generated = declarations.Any(location => location.Generated) || symbol.Locations.Any(location =>
            location.SourceTree is not null && documents.TryGetValue(location.SourceTree, out var document) && document.Kind == "source-generated");
        var constraints = new List<string>();
        var visibility = 31;
        for (var current = symbol; current is not null && current is not INamespaceSymbol; current = current.ContainingType)
        {
            constraints.Add(SemanticId(current) + ":" + current.DeclaredAccessibility);
            visibility &= AccessMask(current.DeclaredAccessibility);
        }
        var associated = symbol switch
        {
            IMethodSymbol method => method.AssociatedSymbol,
            IFieldSymbol field => field.AssociatedSymbol,
            _ => null,
        };
        var details = new Dictionary<string, object?>
        {
            ["namespace"] = symbol.ContainingNamespace?.ToDisplayString(),
            ["isStatic"] = symbol.IsStatic,
            ["isAbstract"] = symbol.IsAbstract,
            ["isSealed"] = symbol.IsSealed,
            ["isVirtual"] = symbol.IsVirtual,
            ["isOverride"] = symbol.IsOverride,
            ["attributes"] = symbol.GetAttributes().Select(attribute => new
            {
                Type = attribute.AttributeClass?.ToDisplayString(Display),
                Constructor = attribute.AttributeConstructor is null ? null : SemanticId(attribute.AttributeConstructor),
                Arguments = attribute.ConstructorArguments.Select(argument => argument.ToCSharpString()).ToArray(),
                NamedArguments = attribute.NamedArguments.Select(argument => new { argument.Key, Value = argument.Value.ToCSharpString() }).ToArray(),
            }).ToArray(),
        };
        foreach (var attribute in symbol.GetAttributes())
        {
            AddTypeEdges(symbol, "attribute", attribute.AttributeClass);
            foreach (var argument in attribute.ConstructorArguments.Concat(attribute.NamedArguments.Select(pair => pair.Value)))
                AttributeArgumentEdges(symbol, argument);
        }
        switch (symbol)
        {
            case INamedTypeSymbol type:
                details["typeKind"] = type.TypeKind.ToString();
                details["arity"] = type.Arity;
                details["isRecord"] = type.IsRecord;
                details["isRefLike"] = type.IsRefLikeType;
                details["isReadOnly"] = type.IsReadOnly;
                details["isExtension"] = type.IsExtension;
                details["typeParameters"] = TypeParameters(type.TypeParameters);
                details["baseType"] = type.BaseType?.ToDisplayString(Display);
                details["interfaces"] = type.Interfaces.Select(value => value.ToDisplayString(Display)).ToArray();
                details["allInterfaces"] = type.AllInterfaces.Select(value => value.ToDisplayString(Display)).Order(StringComparer.Ordinal).ToArray();
                if (type.ExtensionParameter is not null)
                {
                    details["extensionParameter"] = Parameters([type.ExtensionParameter]);
                    AddTypeEdges(type, "extension-receiver", type.ExtensionParameter.Type);
                }
                AddInheritanceEdge(type, "base", type.BaseType);
                foreach (var contract in type.Interfaces)
                    AddInheritanceEdge(type, "interface", contract);
                foreach (var parameter in type.TypeParameters)
                    foreach (var constraint in parameter.ConstraintTypes)
                        AddTypeEdges(type, "generic-constraint", constraint);
                break;
            case IMethodSymbol method:
                details["methodKind"] = method.MethodKind.ToString();
                details["arity"] = method.Arity;
                details["isExtensionMethod"] = method.IsExtensionMethod;
                details["isAsync"] = method.IsAsync;
                details["isInitOnly"] = method.IsInitOnly;
                details["isReadOnly"] = method.IsReadOnly;
                details["returnType"] = method.ReturnType.ToDisplayString(Display);
                details["returnRefKind"] = method.RefKind.ToString();
                details["parameters"] = Parameters(method.Parameters);
                details["typeParameters"] = TypeParameters(method.TypeParameters);
                details["explicitInterfaceImplementations"] = method.ExplicitInterfaceImplementations.Select(SemanticId).ToArray();
                AddTypeEdges(method, "return", method.ReturnType);
                foreach (var parameter in method.Parameters)
                    ParameterEdges(method, parameter);
                foreach (var parameter in method.TypeParameters)
                    foreach (var constraint in parameter.ConstraintTypes)
                        AddTypeEdges(method, "generic-constraint", constraint);
                foreach (var implementation in method.ExplicitInterfaceImplementations)
                    AddEdge(method, "explicit-interface-implementation", implementation);
                if (method.OverriddenMethod is not null)
                    AddEdge(method, "override", method.OverriddenMethod);
                break;
            case IPropertySymbol property:
                details["type"] = property.Type.ToDisplayString(Display);
                details["refKind"] = property.RefKind.ToString();
                details["isIndexer"] = property.IsIndexer;
                details["isRequired"] = property.IsRequired;
                details["isReadOnly"] = property.IsReadOnly;
                details["isInitOnly"] = property.SetMethod?.IsInitOnly ?? false;
                details["parameters"] = Parameters(property.Parameters);
                details["getter"] = property.GetMethod is null ? null : SemanticId(property.GetMethod);
                details["setter"] = property.SetMethod is null ? null : SemanticId(property.SetMethod);
                details["explicitInterfaceImplementations"] = property.ExplicitInterfaceImplementations.Select(SemanticId).ToArray();
                AddTypeEdges(property, "property-type", property.Type);
                foreach (var parameter in property.Parameters)
                    ParameterEdges(property, parameter);
                foreach (var implementation in property.ExplicitInterfaceImplementations)
                    AddEdge(property, "explicit-interface-implementation", implementation);
                if (property.OverriddenProperty is not null)
                    AddEdge(property, "override", property.OverriddenProperty);
                break;
            case IFieldSymbol field:
                details["type"] = field.Type.ToDisplayString(Display);
                details["refKind"] = field.RefKind.ToString();
                details["isConst"] = field.IsConst;
                details["constantValue"] = field.HasConstantValue ? field.ConstantValue : null;
                details["isReadOnly"] = field.IsReadOnly;
                details["isVolatile"] = field.IsVolatile;
                details["isRequired"] = field.IsRequired;
                AddTypeEdges(field, "field-type", field.Type);
                break;
            case IEventSymbol @event:
                details["type"] = @event.Type.ToDisplayString(Display);
                details["explicitInterfaceImplementations"] = @event.ExplicitInterfaceImplementations.Select(SemanticId).ToArray();
                AddTypeEdges(@event, "event-type", @event.Type);
                foreach (var implementation in @event.ExplicitInterfaceImplementations)
                    AddEdge(@event, "explicit-interface-implementation", implementation);
                if (@event.OverriddenEvent is not null)
                    AddEdge(@event, "override", @event.OverriddenEvent);
                break;
        }
        Symbols.Add(new SymbolRow
        {
            Id = id,
            SemanticId = SemanticId(symbol),
            Project = project,
            Framework = framework,
            Assembly = assembly,
            Kind = symbol.Kind.ToString(),
            Name = symbol.Name,
            MetadataName = symbol.MetadataName,
            Signature = symbol.ToDisplayString(Display),
            DeclaredAccessibility = symbol.DeclaredAccessibility.ToString(),
            EffectiveAccessibility = MaskAccess(visibility),
            AccessibilityConstraints = constraints.ToArray(),
            Origin = symbol.IsImplicitlyDeclared ? "compiler-implicit" : generated ? "source-generated" : "explicit-source",
            IsImplicit = symbol.IsImplicitlyDeclared,
            HasGeneratedSource = generated,
            ExternallyVisible = (visibility & (8 | 16)) != 0,
            NonSealedClass = symbol is INamedTypeSymbol { TypeKind: TypeKind.Class, IsStatic: false, IsSealed: false, IsExtension: false },
            HasPublicInstanceConstructor = symbol is INamedTypeSymbol named && named.InstanceConstructors.Any(constructor => constructor.DeclaredAccessibility == Accessibility.Public),
            AssociatedSymbol = associated is null ? null : SemanticId(associated),
            ContainingSymbol = symbol.ContainingType is null ? null : SemanticId(symbol.ContainingType),
            Details = details,
            Declarations = declarations,
            Locations = symbol.Locations.Where(location => location.IsInSource).Select(location =>
            {
                var source = documents[location.SourceTree!];
                var span = location.GetLineSpan();
                return new SourceEvidence(source.Path, span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1,
                    location.SourceSpan.Start, location.SourceSpan.Length,
                    InventoryReport.Hash(location.SourceTree!.GetText().ToString(location.SourceSpan)), source.Kind == "source-generated");
            }).Distinct().OrderBy(location => location.Path, StringComparer.Ordinal).ThenBy(location => location.Start).ToArray(),
        });
        if (symbol is INamedTypeSymbol container)
            foreach (var member in container.GetMembers())
                Visit(member);
    }

    private static IEnumerable<SyntaxReference> SyntaxReferences(ISymbol symbol)
    {
        var implementation = symbol switch
        {
            IMethodSymbol method => (ISymbol?)method.PartialImplementationPart,
            IPropertySymbol property => property.PartialImplementationPart,
            IEventSymbol @event => @event.PartialImplementationPart,
            _ => null,
        };
        return symbol.DeclaringSyntaxReferences.Concat(implementation?.DeclaringSyntaxReferences ?? []);
    }

    private static object[] Parameters(IEnumerable<IParameterSymbol> parameters) => parameters.Select(parameter => (object)new
    {
        parameter.Name,
        parameter.Ordinal,
        Type = parameter.Type.ToDisplayString(Display),
        RefKind = parameter.RefKind.ToString(),
        ScopedKind = parameter.ScopedKind.ToString(),
        parameter.IsParams,
        parameter.IsOptional,
        parameter.HasExplicitDefaultValue,
        DefaultValue = parameter.HasExplicitDefaultValue ? parameter.ExplicitDefaultValue : null,
        Attributes = parameter.GetAttributes().Select(attribute => attribute.ToString()).ToArray(),
    }).ToArray();

    private static object[] TypeParameters(IEnumerable<ITypeParameterSymbol> parameters) => parameters.Select(parameter => (object)new
    {
        parameter.Name,
        parameter.Ordinal,
        Variance = parameter.Variance.ToString(),
        parameter.HasReferenceTypeConstraint,
        ReferenceTypeConstraintNullableAnnotation = parameter.ReferenceTypeConstraintNullableAnnotation.ToString(),
        parameter.HasValueTypeConstraint,
        parameter.HasUnmanagedTypeConstraint,
        parameter.HasNotNullConstraint,
        parameter.HasConstructorConstraint,
        parameter.AllowsRefLikeType,
        ConstraintTypes = parameter.ConstraintTypes.Select(type => type.ToDisplayString(Display)).ToArray(),
    }).ToArray();

    private void ParameterEdges(ISymbol source, IParameterSymbol parameter)
    {
        AddTypeEdges(source, "parameter:" + parameter.Name, parameter.Type);
        foreach (var attribute in parameter.GetAttributes())
            AddTypeEdges(source, "parameter-attribute", attribute.AttributeClass);
    }

    private void AttributeArgumentEdges(ISymbol source, TypedConstant argument)
    {
        if (argument.Kind == TypedConstantKind.Array && !argument.IsNull)
            foreach (var value in argument.Values)
                AttributeArgumentEdges(source, value);
        else if (argument.Value is ITypeSymbol type)
            AddTypeEdges(source, "attribute-type-argument", type);
    }

    private void AddTypeEdges(ISymbol source, string kind, ITypeSymbol? type)
    {
        var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        VisitType(type);
        return;

        void VisitType(ITypeSymbol? value)
        {
            if (value is null || !visited.Add(value))
                return;
            switch (value)
            {
                case INamedTypeSymbol named:
                    AddEdge(source, kind, named.OriginalDefinition);
                    foreach (var argument in named.TypeArguments)
                        VisitType(argument);
                    if (named.ContainingType is not null)
                        VisitType(named.ContainingType);
                    break;
                case IArrayTypeSymbol array:
                    VisitType(array.ElementType);
                    break;
                case IPointerTypeSymbol pointer:
                    VisitType(pointer.PointedAtType);
                    break;
                case IFunctionPointerTypeSymbol pointer:
                    VisitType(pointer.Signature.ReturnType);
                    foreach (var parameter in pointer.Signature.Parameters)
                        VisitType(parameter.Type);
                    break;
                case ITypeParameterSymbol parameter:
                    foreach (var constraint in parameter.ConstraintTypes)
                        VisitType(constraint);
                    break;
                case IDynamicTypeSymbol:
                    break; // dynamic has no assembly identity; the full signature records it.
                default:
                    throw new InvalidOperationException($"Unaccounted signature type {value.Kind}: {value}");
            }
        }
    }

    private void AddInheritanceEdge(ISymbol source, string kind, INamedTypeSymbol? type)
    {
        if (type is null)
            return;
        // A type argument or containing type is a dependency, never a base class
        // or an implemented interface of the source type.
        AddEdge(source, kind, type.OriginalDefinition);
        foreach (var argument in type.TypeArguments)
            AddTypeEdges(source, kind + "-type-dependency", argument);
        AddTypeEdges(source, kind + "-type-dependency", type.ContainingType);
    }

    private void AddEdge(ISymbol source, string kind, ISymbol target) => Relations.Add(new Relation(
        Id(source), kind, SemanticId(target.OriginalDefinition), target.ContainingAssembly?.Name ?? assembly, target.DeclaredAccessibility.ToString()));
    private string Id(ISymbol symbol) => project + "@" + framework + "|" + SemanticId(symbol);
    private static string SemanticId(ISymbol symbol) => (symbol.ContainingAssembly?.Name ?? "<no-assembly>") + "|" +
        (symbol.GetDocumentationCommentId() ?? "fallback:" + symbol.Kind + ":" + symbol.MetadataName + ":" + symbol.ToDisplayString(Display));

    // Preserve the containing constraints as well as their accessibility envelope.
    // Protected constraints on different nested owners still require caller-specific
    // reasoning; this envelope is not a proof that any particular external caller works.
    private static int AccessMask(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public or Accessibility.NotApplicable => 31,
        Accessibility.ProtectedOrInternal => 15,
        Accessibility.Protected => 11,
        Accessibility.Internal => 7,
        Accessibility.ProtectedAndInternal => 3,
        Accessibility.Private => 1,
        _ => throw new InvalidOperationException($"Unknown accessibility: {accessibility}"),
    };
    private static string MaskAccess(int mask) => mask switch
    {
        31 => "Public",
        15 => "ProtectedOrInternal",
        11 => "Protected",
        7 => "Internal",
        3 => "ProtectedAndInternal",
        1 => "Private",
        _ => throw new InvalidOperationException($"Unknown effective accessibility mask: {mask}"),
    };
}
