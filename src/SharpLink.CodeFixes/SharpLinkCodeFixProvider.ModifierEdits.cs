namespace SharpLink.CodeFixes;

internal sealed partial class SharpLinkCodeFixProvider
{
    private static BaseTypeDeclarationSyntax MakePublic(BaseTypeDeclarationSyntax declaration)
        => declaration.WithModifiers(WithAccessibility(declaration.Modifiers, SyntaxKind.PublicKeyword));

    private static DelegateDeclarationSyntax MakePublic(DelegateDeclarationSyntax declaration)
        => declaration.WithModifiers(WithAccessibility(declaration.Modifiers, SyntaxKind.PublicKeyword));

    private static MemberDeclarationSyntax MakePublic(MemberDeclarationSyntax declaration)
        => declaration switch
        {
            BaseTypeDeclarationSyntax type => MakePublic(type),
            DelegateDeclarationSyntax @delegate => MakePublic(@delegate),
            _ => declaration
        };

    private static SyntaxTokenList WithAccessibility(SyntaxTokenList modifiers, SyntaxKind accessibility)
    {
        var accessibilityModifiers = modifiers.Where(IsAccessibilityModifier).ToArray();
        if (accessibilityModifiers.Length == 0)
            return modifiers.Insert(0, SyntaxFactory.Token(accessibility));

        var first = accessibilityModifiers[0];
        var trailingTrivia = first.TrailingTrivia;
        foreach (var removed in accessibilityModifiers.Skip(1))
        {
            trailingTrivia = trailingTrivia
                .AddRange(removed.LeadingTrivia)
                .AddRange(removed.TrailingTrivia);
        }

        var replacement = SyntaxFactory.Token(accessibility)
            .WithLeadingTrivia(first.LeadingTrivia)
            .WithTrailingTrivia(trailingTrivia);
        var updated = new List<SyntaxToken>(modifiers.Count - accessibilityModifiers.Length + 1);
        foreach (var modifier in modifiers)
        {
            if (!IsAccessibilityModifier(modifier))
                updated.Add(modifier);
            else if (modifier == first)
                updated.Add(replacement);
        }
        return new SyntaxTokenList(updated);

        static bool IsAccessibilityModifier(SyntaxToken token)
            => token.Kind() is SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword or
                SyntaxKind.ProtectedKeyword or SyntaxKind.InternalKeyword or SyntaxKind.FileKeyword;
    }

    private static TypeDeclarationSyntax AddModifier(TypeDeclarationSyntax declaration, SyntaxKind modifier)
        => declaration.WithModifiers(AddModifier(declaration.Modifiers, modifier));

    private static SyntaxTokenList AddModifier(SyntaxTokenList modifiers, SyntaxKind modifier)
    {
        if (modifiers.Any(modifier))
            return modifiers;
        for (var index = 0; index < modifiers.Count; index++)
        {
            if (modifiers[index].IsKind(SyntaxKind.PartialKeyword))
                return modifiers.Insert(index, SyntaxFactory.Token(modifier));
        }
        return modifiers.Add(SyntaxFactory.Token(modifier));
    }

    private static TypeDeclarationSyntax RemoveModifier(TypeDeclarationSyntax declaration, SyntaxKind modifier)
    {
        var removed = declaration.Modifiers.FirstOrDefault(token => token.IsKind(modifier));
        var modifiers = RemoveModifier(declaration.Modifiers, modifier);
        var updated = declaration.WithModifiers(modifiers);
        return removed.RawKind == 0 || modifiers.Count != 0
            ? updated
            : updated.WithKeyword(updated.Keyword.WithLeadingTrivia(
                removed.LeadingTrivia.AddRange(removed.TrailingTrivia)
                    .AddRange(updated.Keyword.LeadingTrivia)));
    }

    private static MethodDeclarationSyntax RemoveModifier(
        MethodDeclarationSyntax declaration,
        SyntaxKind modifier)
    {
        var removed = declaration.Modifiers.FirstOrDefault(token => token.IsKind(modifier));
        var modifiers = RemoveModifier(declaration.Modifiers, modifier);
        var updated = declaration.WithModifiers(modifiers);
        return removed.RawKind == 0 || modifiers.Count != 0
            ? updated
            : updated.WithReturnType(updated.ReturnType.WithLeadingTrivia(
                removed.LeadingTrivia.AddRange(removed.TrailingTrivia)
                    .AddRange(updated.ReturnType.GetLeadingTrivia())));
    }

    private static SyntaxTokenList RemoveModifier(SyntaxTokenList modifiers, SyntaxKind modifier)
    {
        var index = -1;
        for (var current = 0; current < modifiers.Count; current++)
        {
            if (modifiers[current].IsKind(modifier))
            {
                index = current;
                break;
            }
        }
        if (index < 0)
            return modifiers;

        var removed = modifiers[index];
        var updated = modifiers.RemoveAt(index);
        if (updated.Count == 0)
            return updated;
        if (index < updated.Count)
        {
            var next = updated[index];
            return updated.Replace(next, next.WithLeadingTrivia(
                removed.LeadingTrivia.AddRange(removed.TrailingTrivia)
                    .AddRange(next.LeadingTrivia)));
        }

        var previous = updated[index - 1];
        return updated.Replace(previous, previous.WithTrailingTrivia(
            previous.TrailingTrivia.AddRange(removed.LeadingTrivia)
                .AddRange(removed.TrailingTrivia)));
    }
}
