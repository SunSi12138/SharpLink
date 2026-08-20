using System.Runtime.CompilerServices;
using SharpLink.Runtime;

namespace SharpLink.PackageSmoke;

internal static class PreCreditPackageSurface
{
    [ModuleInitializer]
    internal static void Verify()
    {
        var optionsType = typeof(SharpLinkFlowControlOptions);
        var propertyName = nameof(SharpLinkFlowControlOptions.MaxPreCreditSerializedBytes);
        var property = optionsType.GetProperty(propertyName);

        if (property is null ||
            property.DeclaringType != optionsType ||
            property.PropertyType != typeof(int) ||
            property.GetMethod is not { IsPublic: true, IsStatic: false } ||
            property.SetMethod is not { IsPublic: true, IsStatic: false })
        {
            throw new InvalidOperationException(
                $"Packed Runtime API surface must expose {optionsType.FullName}.{propertyName} " +
                "as a public instance int property with public get/set accessors.");
        }
    }
}
