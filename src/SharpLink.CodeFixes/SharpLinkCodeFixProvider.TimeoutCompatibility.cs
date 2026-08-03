namespace SharpLink.CodeFixes;

internal sealed partial class SharpLinkCodeFixProvider
{
    private static async Task RegisterTimeoutFixesAsync(CodeFixContext context, Diagnostic diagnostic)
    {
        var declaration = await FindNodeAsync<MethodDeclarationSyntax>(
            context.Document, diagnostic, context.CancellationToken).ConfigureAwait(false);
        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);
        if (declaration is null ||
            semanticModel?.GetDeclaredSymbol(declaration, context.CancellationToken) is not IMethodSymbol method ||
            IsObsoleteWithError(method))
        {
            return;
        }

        var equivalentMethods = await FindEquivalentInterfaceMethodsAsync(
            method, context.Document.Project.Solution, context.CancellationToken).ConfigureAwait(false);
        var timeoutAttributes = equivalentMethods
            .SelectMany(static candidate => candidate.GetAttributes())
            .Where(IsTimeoutAttribute)
            .Where(static attribute =>
                TryGetTimeoutSeconds(attribute, out var seconds) && !IsValidTimeoutSeconds(seconds))
            .ToImmutableArray();
        if (timeoutAttributes.Length == 0 ||
            timeoutAttributes.Any(attribute =>
                attribute.ApplicationSyntaxReference is not { } reference ||
                !IsRegularEditableDocument(context.Document.Project.Solution, reference.SyntaxTree)))
        {
            return;
        }

        var references = timeoutAttributes
            .Select(static attribute => attribute.ApplicationSyntaxReference!)
            .ToImmutableArray();
        if (await CanUseParameterlessTimeoutAttributeAsync(
                context.Document.Project.Solution, timeoutAttributes, context.CancellationToken)
            .ConfigureAwait(false))
        {
            RegisterSolutionFix(
                context,
                diagnostic,
                "Use generated default timeout",
                "UseDefaultTimeout",
                (solution, _, _, ct) => UpdateTimeoutAttributesAsync(solution, references, remove: false, ct));
        }
        RegisterSolutionFix(
            context,
            diagnostic,
            "Remove [Timeout]",
            "RemoveTimeout",
            (solution, _, _, ct) => UpdateTimeoutAttributesAsync(solution, references, remove: true, ct));
    }

    private static async Task<bool> CanUseParameterlessTimeoutAttributeAsync(
        Solution solution,
        ImmutableArray<AttributeData> attributes,
        CancellationToken cancellationToken)
    {
        foreach (var attribute in attributes)
        {
            var reference = attribute.ApplicationSyntaxReference;
            var document = reference is null ? null : solution.GetDocument(reference.SyntaxTree);
            var semanticModel = document is null
                ? null
                : await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (reference is null || semanticModel is null || attribute.AttributeClass is not { } attributeClass ||
                !attributeClass.InstanceConstructors.Any(constructor =>
                    constructor.Parameters.Length == 0 &&
                    !IsObsoleteWithError(constructor) &&
                    semanticModel.IsAccessible(reference.Span.Start, constructor)))
            {
                return false;
            }
        }
        return true;
    }

    private static async Task<Solution> UpdateTimeoutAttributesAsync(
        Solution solution,
        ImmutableArray<SyntaxReference> references,
        bool remove,
        CancellationToken cancellationToken)
    {
        if (remove)
            return await RemoveAttributesAsync(solution, references, cancellationToken).ConfigureAwait(false);

        var referencesByDocument = references
            .Select(reference => (Reference: reference, Document: solution.GetDocument(reference.SyntaxTree)))
            .Where(static item => item.Document is not null)
            .GroupBy(static item => item.Document!.Id);
        foreach (var group in referencesByDocument)
        {
            var document = solution.GetDocument(group.Key);
            var root = document is null ? null : await document.GetSyntaxRootAsync(cancellationToken)
                .ConfigureAwait(false);
            if (root is null)
                continue;

            var attributes = group
                .Select(item => root.FindNode(item.Reference.Span, getInnermostNodeForTie: true)
                    .AncestorsAndSelf().OfType<AttributeSyntax>().FirstOrDefault())
                .Where(static attribute => attribute is not null)
                .Select(static attribute => attribute!)
                .Distinct()
                .ToArray();
            root = root.ReplaceNodes(attributes, static (_, current) =>
                UseParameterlessAttributeConstructor(current).WithAdditionalAnnotations(Formatter.Annotation));
            solution = solution.WithDocumentSyntaxRoot(group.Key, root);
        }
        return solution;
    }

    private static AttributeSyntax UseParameterlessAttributeConstructor(AttributeSyntax attribute)
    {
        var argumentList = attribute.ArgumentList;
        if (argumentList is null)
            return attribute;

        var arguments = argumentList.Arguments;
        foreach (var argument in argumentList.Arguments.Where(static item => item.NameEquals is null).ToArray())
            arguments = arguments.Remove(argument);
        return attribute.WithArgumentList(arguments.Count == 0 ? null : argumentList.WithArguments(arguments));
    }
}
