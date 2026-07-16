namespace SharpLink.Runtime;

/// <summary>Configures striped state containers owned by one runtime context.</summary>
public sealed class RuntimeConcurrencyOptions
{
    /// <summary>Gets or sets the number of stripes. Must be a power of two.</summary>
    public int StripeCount { get; set; } = 32;

    /// <summary>Gets or sets the initial dictionary capacity allocated per stripe.</summary>
    public int InitialMapCapacityPerStripe { get; set; } = 8;

    /// <summary>Validates the stripe and capacity settings.</summary>
    public void Validate()
    {
        if (StripeCount <= 0 || (StripeCount & (StripeCount - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(StripeCount), "StripeCount must be a positive power of two.");
        ArgumentOutOfRangeException.ThrowIfNegative(InitialMapCapacityPerStripe);
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
