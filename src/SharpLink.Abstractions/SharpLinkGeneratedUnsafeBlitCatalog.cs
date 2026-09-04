using System.Runtime.CompilerServices;

namespace SharpLink.Abstractions;

/// <summary>Describes runtime ABI checks already resolved for one generated UnsafeBlit payload.</summary>
public readonly record struct SharpLinkGeneratedUnsafeBlitRequirement(
    int NativePointerWidth,
    bool RequiresDateTimeOffsetRawAbi);

/// <summary>
/// Publishes source-generated UnsafeBlit ABI requirements without retaining collectible payload Types.
/// </summary>
public static class SharpLinkGeneratedUnsafeBlitCatalog
{
    private static readonly ConditionalWeakTable<Type, RequirementBox> Requirements = new();

    /// <summary>Registers the resolved UnsafeBlit ABI requirement for one closed payload Type.</summary>
    public static void Register(
        Type targetType,
        int nativePointerWidth,
        bool requiresDateTimeOffsetRawAbi)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        if (nativePointerWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(nativePointerWidth));

        var incoming = new SharpLinkGeneratedUnsafeBlitRequirement(
            nativePointerWidth,
            requiresDateTimeOffsetRawAbi);
        var stored = Requirements.GetValue(targetType, _ => new RequirementBox(incoming));
        if (stored.Requirement != incoming)
        {
            throw new InvalidOperationException(
                $"Generated UnsafeBlit ABI requirements for '{targetType.FullName}' are inconsistent.");
        }
    }

    /// <summary>Attempts to read the generated UnsafeBlit ABI requirement for one closed payload Type.</summary>
    public static bool TryGet(
        Type targetType,
        out SharpLinkGeneratedUnsafeBlitRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        if (Requirements.TryGetValue(targetType, out var stored))
        {
            requirement = stored.Requirement;
            return true;
        }

        requirement = default;
        return false;
    }

    private sealed class RequirementBox(SharpLinkGeneratedUnsafeBlitRequirement requirement)
    {
        internal SharpLinkGeneratedUnsafeBlitRequirement Requirement { get; } = requirement;
    }
}
