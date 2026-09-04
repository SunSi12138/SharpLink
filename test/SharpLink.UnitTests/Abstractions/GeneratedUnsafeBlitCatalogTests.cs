using SharpLink.Abstractions;

namespace SharpLink.UnitTests.Abstractions;

public sealed class GeneratedUnsafeBlitCatalogTests
{
    [Test]
    public void RequirementRegistrationShouldBeWeakKeyedAndDeterministic()
    {
        SharpLinkGeneratedUnsafeBlitCatalog.Register(
            typeof(CatalogPayload),
            nativePointerWidth: 8,
            requiresDateTimeOffsetRawAbi: true);
        SharpLinkGeneratedUnsafeBlitCatalog.Register(
            typeof(CatalogPayload),
            nativePointerWidth: 8,
            requiresDateTimeOffsetRawAbi: true);

        if (!SharpLinkGeneratedUnsafeBlitCatalog.TryGet(typeof(CatalogPayload), out var requirement) ||
            requirement.NativePointerWidth != 8 ||
            !requirement.RequiresDateTimeOffsetRawAbi)
        {
            throw new InvalidOperationException("Generated UnsafeBlit requirement was not retained accurately.");
        }

        try
        {
            SharpLinkGeneratedUnsafeBlitCatalog.Register(
                typeof(CatalogPayload),
                nativePointerWidth: 4,
                requiresDateTimeOffsetRawAbi: true);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException("Conflicting generated UnsafeBlit requirements must fail closed.");
    }

    private readonly record struct CatalogPayload(DateTimeOffset Value);
}
