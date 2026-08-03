namespace SharpLink.CodeFixes;

internal sealed partial class SharpLinkCodeFixProvider
{
    private static SeparatedSyntaxList<TNode> RebuildSeparatedListPreservingTrivia<TNode>(
        SeparatedSyntaxList<TNode> original,
        IEnumerable<(int ordinal, TNode node)> retained,
        TNode? appended = null)
        where TNode : SyntaxNode
    {
        var items = retained.ToArray();
        var positions = items
            .Select(static (item, position) => (item.ordinal, position))
            .ToDictionary(static item => item.ordinal, static item => item.position);
        var nodes = items.Select(static item => item.node).ToList();
        if (appended is not null)
            nodes.Add(appended);

        var separators = new SyntaxToken[Math.Max(0, nodes.Count - 1)];
        var separatorSources = Enumerable.Repeat(-1, separators.Length).ToArray();
        var usedSeparators = new bool[original.SeparatorCount];
        for (var boundary = 0; boundary < separators.Length; boundary++)
        {
            if (boundary < items.Length && items[boundary].ordinal < original.SeparatorCount)
            {
                var separatorIndex = items[boundary].ordinal;
                separators[boundary] = original.GetSeparator(separatorIndex);
                separatorSources[boundary] = separatorIndex;
                usedSeparators[separatorIndex] = true;
            }
            else
            {
                separators[boundary] = SyntaxFactory.Token(SyntaxKind.CommaToken);
            }
        }

        for (var separatorIndex = 0; separatorIndex < original.SeparatorCount; separatorIndex++)
        {
            if (usedSeparators[separatorIndex])
                continue;
            var orphaned = original.GetSeparator(separatorIndex);
            if (separators.Length == 0)
            {
                if (nodes.Count != 0)
                {
                    nodes[0] = (TNode)nodes[0].WithTrailingTrivia(
                        nodes[0].GetTrailingTrivia()
                            .AddRange(orphaned.LeadingTrivia)
                            .AddRange(orphaned.TrailingTrivia));
                }
                continue;
            }

            int boundary;
            if (positions.TryGetValue(separatorIndex, out var leftPosition))
            {
                boundary = Math.Min(leftPosition, separators.Length - 1);
            }
            else
            {
                var nextOrdinal = positions.Keys
                    .Where(ordinal => ordinal > separatorIndex)
                    .DefaultIfEmpty(int.MaxValue)
                    .Min();
                boundary = nextOrdinal == int.MaxValue
                    ? separators.Length - 1
                    : Math.Max(0, positions[nextOrdinal] - 1);
            }

            if (separatorSources[boundary] < 0)
            {
                separators[boundary] = orphaned;
                separatorSources[boundary] = separatorIndex;
            }
            else
            {
                separators[boundary] = separators[boundary]
                    .WithLeadingTrivia(separators[boundary].LeadingTrivia.AddRange(orphaned.LeadingTrivia))
                    .WithTrailingTrivia(MergeSeparatorTrailingTrivia(
                        separators[boundary].TrailingTrivia,
                        orphaned.TrailingTrivia));
            }
        }

        var nodesAndTokens = new List<SyntaxNodeOrToken>(nodes.Count + separators.Length);
        for (var index = 0; index < nodes.Count; index++)
        {
            nodesAndTokens.Add(nodes[index]);
            if (index < separators.Length)
                nodesAndTokens.Add(separators[index]);
        }
        return SyntaxFactory.SeparatedList<TNode>(nodesAndTokens);
    }

    private static SyntaxTriviaList MergeSeparatorTrailingTrivia(
        SyntaxTriviaList existing,
        SyntaxTriviaList additional)
    {
        var merged = existing.AddRange(additional).ToArray();
        var commentIndex = Array.FindIndex(merged, static trivia =>
            trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
            trivia.IsKind(SyntaxKind.MultiLineCommentTrivia));
        if (commentIndex < 0 || !merged.Take(commentIndex).Any(static trivia =>
                trivia.IsKind(SyntaxKind.EndOfLineTrivia)))
        {
            return SyntaxFactory.TriviaList(merged);
        }

        var reordered = new List<SyntaxTrivia>(merged.Length);
        reordered.AddRange(merged.Take(commentIndex).Where(static trivia =>
            !trivia.IsKind(SyntaxKind.EndOfLineTrivia)));
        reordered.Add(merged[commentIndex]);
        reordered.AddRange(merged.Take(commentIndex).Where(static trivia =>
            trivia.IsKind(SyntaxKind.EndOfLineTrivia)));
        reordered.AddRange(merged.Skip(commentIndex + 1));
        return SyntaxFactory.TriviaList(reordered);
    }

    private static ArgumentSyntax NameArgument(
        ArgumentSyntax argument,
        int ordinal,
        InvocationEdit edit)
    {
        if (ordinal < 0 || ordinal >= edit.ParameterNames.Length)
            return argument;
        var replacement = CreateNameColon(edit.ParameterNames[ordinal]);
        if (argument.NameColon is { } existing)
        {
            replacement = existing.WithName(
                replacement.Name.WithTriviaFrom(existing.Name));
        }
        return argument.WithNameColon(replacement);
    }

    private static NameColonSyntax CreateNameColon(string parameterName)
    {
        var escaped = SyntaxFacts.GetKeywordKind(parameterName) == SyntaxKind.None
            ? parameterName
            : "@" + parameterName;
        return SyntaxFactory.NameColon(
            SyntaxFactory.IdentifierName(SyntaxFactory.ParseToken(escaped)));
    }
}
