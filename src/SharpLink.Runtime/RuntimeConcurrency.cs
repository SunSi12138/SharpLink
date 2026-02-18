namespace SharpLink.Runtime;

public static class RuntimeConcurrency
{
    private static int _stripeCount = 32;
    private static int _initialMapCapacityPerStripe = 8;

    public static void Configure(Action<RuntimeConcurrencyOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new RuntimeConcurrencyOptions
        {
            StripeCount = Volatile.Read(ref _stripeCount),
            InitialMapCapacityPerStripe = Volatile.Read(ref _initialMapCapacityPerStripe)
        };

        configure(options);
        options.Validate();

        Interlocked.Exchange(ref _stripeCount, options.StripeCount);
        Interlocked.Exchange(ref _initialMapCapacityPerStripe, options.InitialMapCapacityPerStripe);
    }

    internal static RuntimeConcurrencySnapshot Snapshot()
    {
        return new RuntimeConcurrencySnapshot(
            Volatile.Read(ref _stripeCount),
            Volatile.Read(ref _initialMapCapacityPerStripe));
    }
}

internal readonly record struct RuntimeConcurrencySnapshot(int StripeCount, int InitialMapCapacityPerStripe);
