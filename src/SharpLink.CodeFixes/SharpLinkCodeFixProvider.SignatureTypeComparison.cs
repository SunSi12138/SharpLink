namespace SharpLink.CodeFixes;

internal sealed partial class SharpLinkCodeFixProvider
{
    private static bool AreSignatureTypesEquivalent(
        ITypeSymbol left,
        IMethodSymbol leftMethod,
        ITypeSymbol right,
        IMethodSymbol rightMethod)
    {
        if (left is ITypeParameterSymbol leftParameter &&
            SymbolEqualityComparer.Default.Equals(leftParameter.ContainingSymbol, leftMethod))
        {
            return right is ITypeParameterSymbol rightParameter &&
                   SymbolEqualityComparer.Default.Equals(rightParameter.ContainingSymbol, rightMethod) &&
                   leftParameter.Ordinal == rightParameter.Ordinal;
        }
        if (left is IArrayTypeSymbol leftArray && right is IArrayTypeSymbol rightArray)
        {
            return leftArray.Rank == rightArray.Rank &&
                   AreSignatureTypesEquivalent(
                       leftArray.ElementType,
                       leftMethod,
                       rightArray.ElementType,
                       rightMethod);
        }
        if (left is IPointerTypeSymbol leftPointer && right is IPointerTypeSymbol rightPointer)
        {
            return AreSignatureTypesEquivalent(
                leftPointer.PointedAtType,
                leftMethod,
                rightPointer.PointedAtType,
                rightMethod);
        }
        if (left is IFunctionPointerTypeSymbol leftFunction &&
            right is IFunctionPointerTypeSymbol rightFunction)
        {
            return AreFunctionPointerSignaturesEquivalent(
                leftFunction.Signature,
                leftMethod,
                rightFunction.Signature,
                rightMethod);
        }
        if (left is INamedTypeSymbol leftNamed && right is INamedTypeSymbol rightNamed)
        {
            return SymbolEqualityComparer.Default.Equals(
                       leftNamed.OriginalDefinition,
                       rightNamed.OriginalDefinition) &&
                   (leftNamed.ContainingType is null && rightNamed.ContainingType is null ||
                    leftNamed.ContainingType is not null && rightNamed.ContainingType is not null &&
                    AreSignatureTypesEquivalent(
                        leftNamed.ContainingType,
                        leftMethod,
                        rightNamed.ContainingType,
                        rightMethod)) &&
                   leftNamed.TypeArguments.Length == rightNamed.TypeArguments.Length &&
                   leftNamed.TypeArguments.Zip(rightNamed.TypeArguments, (leftArgument, rightArgument) =>
                           AreSignatureTypesEquivalent(
                               leftArgument,
                               leftMethod,
                               rightArgument,
                               rightMethod))
                       .All(static equivalent => equivalent);
        }
        return SymbolEqualityComparer.Default.Equals(left, right);
    }

    private static bool AreFunctionPointerSignaturesEquivalent(
        IMethodSymbol left,
        IMethodSymbol leftMethod,
        IMethodSymbol right,
        IMethodSymbol rightMethod)
        => left.CallingConvention == right.CallingConvention &&
           left.RefKind == right.RefKind &&
           left.UnmanagedCallingConventionTypes.Length == right.UnmanagedCallingConventionTypes.Length &&
           left.UnmanagedCallingConventionTypes.Zip(
                   right.UnmanagedCallingConventionTypes,
                   static (leftConvention, rightConvention) =>
                       SymbolEqualityComparer.Default.Equals(leftConvention, rightConvention))
               .All(static equivalent => equivalent) &&
           left.Parameters.Length == right.Parameters.Length &&
           left.Parameters.Zip(right.Parameters, (leftParameter, rightParameter) =>
                   leftParameter.RefKind == rightParameter.RefKind &&
                   AreSignatureTypesEquivalent(
                       leftParameter.Type,
                       leftMethod,
                       rightParameter.Type,
                       rightMethod))
               .All(static equivalent => equivalent) &&
           AreSignatureTypesEquivalent(left.ReturnType, leftMethod, right.ReturnType, rightMethod);
}
