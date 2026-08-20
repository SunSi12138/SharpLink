namespace SharpLink.Runtime;

/// <summary>Configures striped state containers owned by one runtime context.</summary>
public sealed class RuntimeConcurrencyOptions
{
    /// <summary>The hard maximum number of state-store stripes.</summary>
    public const int MaximumStripeCount = 1024;

    /// <summary>The hard maximum aggregate initial entries reserved across all stripes.</summary>
    public const int MaximumInitialMapEntries = 1024 * 1024;

    /// <summary>Gets or sets the number of stripes. Must be a power of two.</summary>
    public int StripeCount { get; set; } = 32;

    /// <summary>Gets or sets the initial dictionary capacity allocated per stripe.</summary>
    public int InitialMapCapacityPerStripe { get; set; } = 8;

    /// <summary>Validates the stripe and capacity settings.</summary>
    public void Validate()
        => Validate(StripeCount, InitialMapCapacityPerStripe);

    internal static void Validate(int stripeCount, int initialMapCapacityPerStripe)
    {
        if (stripeCount <= 0 || stripeCount > MaximumStripeCount ||
            (stripeCount & (stripeCount - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(StripeCount),
                $"StripeCount must be a positive power of two no larger than {MaximumStripeCount}.");
        }
        if (initialMapCapacityPerStripe < 0)
            throw new ArgumentOutOfRangeException(nameof(InitialMapCapacityPerStripe));
        if ((long)stripeCount * initialMapCapacityPerStripe > MaximumInitialMapEntries)
        {
            throw new ArgumentOutOfRangeException(
                nameof(InitialMapCapacityPerStripe),
                $"Aggregate initial map capacity cannot exceed {MaximumInitialMapEntries} entries.");
        }
    }

    /// <summary>Creates a validated copy isolated from later mutations.</summary>
    public RuntimeConcurrencyOptions CloneValidated()
    {
        Validate();
        return new RuntimeConcurrencyOptions
        {
            StripeCount = StripeCount,
            InitialMapCapacityPerStripe = InitialMapCapacityPerStripe
        };
    }
}
