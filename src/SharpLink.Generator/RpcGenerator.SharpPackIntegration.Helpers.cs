namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static bool HasAttribute(ISymbol symbol, string ns, string name)
        => symbol.GetAttributes().Any(attribute => IsAttribute(attribute, ns, name));

    private static string GetTypeName(ITypeSymbol type)
        => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static ITypeSymbol? GetMemberType(ISymbol member)
        => member switch
        {
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            _ => null
        };

    private static string EscapeIdentifier(string identifier)
        => Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(identifier) !=
           Microsoft.CodeAnalysis.CSharp.SyntaxKind.None
            ? "@" + identifier
            : identifier;
}
