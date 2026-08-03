namespace SharpLink.CodeFixes;

internal sealed partial class SharpLinkCodeFixProvider
{
    private static ImmutableArray<AttributeData> GetAttributesAcrossPartialPropertyParts(
        ISymbol target,
        string metadataName)
    {
        var owners = new List<ISymbol> { target };
        if (target is IPropertySymbol property)
        {
            if (property.PartialDefinitionPart is { } definition &&
                !owners.Any(owner => SymbolEqualityComparer.Default.Equals(owner, definition)))
            {
                owners.Add(definition);
            }
            if (property.PartialImplementationPart is { } implementation &&
                !owners.Any(owner => SymbolEqualityComparer.Default.Equals(owner, implementation)))
            {
                owners.Add(implementation);
            }
        }

        var result = new List<AttributeData>();
        foreach (var attribute in owners.SelectMany(static owner => owner.GetAttributes())
                     .Where(attribute => string.Equals(
                         attribute.AttributeClass?.ToDisplayString(), metadataName, StringComparison.Ordinal)))
        {
            var reference = attribute.ApplicationSyntaxReference;
            if (result.Any(existing =>
                    existing.ApplicationSyntaxReference?.SyntaxTree == reference?.SyntaxTree &&
                    existing.ApplicationSyntaxReference?.Span == reference?.Span))
            {
                continue;
            }
            result.Add(attribute);
        }
        return result.ToImmutableArray();
    }

    private static AttributeListSyntax CreateRpcMemberAttributeList(AttributeArgumentSyntax argument)
        => SyntaxFactory.AttributeList(
            SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Attribute(SyntaxFactory.ParseName("global::SharpLink.Sdk.RpcMember"))
                    .WithArgumentList(SyntaxFactory.AttributeArgumentList(
                        SyntaxFactory.SingletonSeparatedList(argument)))));

    private static bool TryGetRpcMemberTarget(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        out ISymbol? target,
        out SyntaxNode? declaration)
    {
        var member = node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(static item => item is PropertyDeclarationSyntax or FieldDeclarationSyntax);
        switch (member)
        {
            case PropertyDeclarationSyntax property:
                target = semanticModel.GetDeclaredSymbol(property, cancellationToken);
                declaration = property;
                return target is not null;
            case FieldDeclarationSyntax { Declaration.Variables.Count: 1 } field:
                target = semanticModel.GetDeclaredSymbol(field.Declaration.Variables[0], cancellationToken);
                declaration = field;
                return target is not null;
        }

        var parameter = node.AncestorsAndSelf().OfType<ParameterSyntax>().FirstOrDefault();
        var record = parameter?.Parent?.Parent as RecordDeclarationSyntax;
        var recordType = record is null
            ? null
            : semanticModel.GetDeclaredSymbol(record, cancellationToken);
        target = parameter is null || recordType is null
            ? null
            : recordType.GetMembers(parameter.Identifier.ValueText).OfType<IPropertySymbol>().FirstOrDefault();
        declaration = target is null ? null : parameter;
        return target is not null;
    }
}
