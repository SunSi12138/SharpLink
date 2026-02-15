namespace SharpLink.Runtime;

public sealed class RuntimeConcurrencyOptions
{
    public int StripeCount { get; set; } = 32;
    public int InitialMapCapacityPerStripe { get; set; } = 8;

    public void Validate()
    {
        if (StripeCount <= 0 || (StripeCount & (StripeCount - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(StripeCount), "StripeCount must be a positive power of two.");
        ArgumentOutOfRangeException.ThrowIfNegative(InitialMapCapacityPerStripe);
    }
}
