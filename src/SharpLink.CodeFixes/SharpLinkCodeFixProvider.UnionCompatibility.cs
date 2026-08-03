namespace SharpLink.CodeFixes;

internal sealed partial class SharpLinkCodeFixProvider
{
    private static async Task<Document> RestoreUnionTagAsync(
        Document document,
        Diagnostic diagnostic,
        string tag,
        string type,
        int? preservedTag,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var attribute = root?.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
            .AncestorsAndSelf().OfType<AttributeSyntax>().FirstOrDefault();
        if (attribute is null || semanticModel is null ||
            !TryGetUnionCaseArguments(
                attribute, semanticModel, cancellationToken, out var existingTag, out var existingType))
        {
            return document;
        }

        var typeName = type.StartsWith("global::", StringComparison.Ordinal) ? type : "global::" + type;
        var restoredTag = existingTag.WithExpression(
            SyntaxFactory.ParseExpression(tag).WithTriviaFrom(existingTag.Expression));
        var restoredTypeExpression = existingType.Expression is TypeOfExpressionSyntax typeOf
            ? typeOf.WithType(SyntaxFactory.ParseTypeName(typeName).WithTriviaFrom(typeOf.Type))
            : SyntaxFactory.TypeOfExpression(SyntaxFactory.ParseTypeName(typeName))
                .WithTriviaFrom(existingType.Expression);
        var tagIndex = attribute.ArgumentList!.Arguments.IndexOf(existingTag);
        var typeIndex = attribute.ArgumentList.Arguments.IndexOf(existingType);
        var arguments = attribute.ArgumentList.Arguments.Replace(
            attribute.ArgumentList.Arguments[tagIndex], restoredTag);
        arguments = arguments.Replace(
            arguments[typeIndex],
            existingType.WithExpression(restoredTypeExpression));
        var updated = attribute.WithArgumentList(attribute.ArgumentList.WithArguments(arguments))
            .WithAdditionalAnnotations(Formatter.Annotation);
        if (preservedTag is not { } newTag || attribute.Parent is not AttributeListSyntax attributeList)
            return await ReplaceNodeAsync(document, attribute, updated, cancellationToken).ConfigureAwait(false);

        var preservedTagArgument = existingTag.WithExpression(
            SyntaxFactory.ParseExpression(newTag.ToString(CultureInfo.InvariantCulture))
                .WithTriviaFrom(existingTag.Expression));
        var preservedArguments = attribute.ArgumentList.Arguments.Replace(
            attribute.ArgumentList.Arguments[tagIndex], preservedTagArgument);
        var preserved = attribute.WithArgumentList(attribute.ArgumentList.WithArguments(preservedArguments))
            .WithoutLeadingTrivia()
            .WithoutTrailingTrivia()
            .WithAdditionalAnnotations(Formatter.Annotation);
        var attributeIndex = attributeList.Attributes.IndexOf(attribute);
        var updatedList = attributeList.WithAttributes(
                attributeList.Attributes.Replace(attribute, updated).Insert(attributeIndex + 1, preserved))
            .WithAdditionalAnnotations(Formatter.Annotation);
        return await ReplaceNodeAsync(document, attributeList, updatedList, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryGetUnionCaseArguments(
        AttributeSyntax attribute,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        out AttributeArgumentSyntax tag,
        out AttributeArgumentSyntax caseType)
    {
        tag = null!;
        caseType = null!;
        if (attribute.ArgumentList is not { Arguments.Count: 2 } argumentList)
            return false;

        foreach (var argument in argumentList.Arguments)
        {
            if (semanticModel.GetOperation(argument, cancellationToken) is not
                IArgumentOperation { Parameter: { } parameter })
            {
                return false;
            }

            if (parameter.Ordinal == 0)
                tag = argument;
            else if (parameter.Ordinal == 1)
                caseType = argument;
        }
        return tag is not null && caseType is not null;
    }
}
