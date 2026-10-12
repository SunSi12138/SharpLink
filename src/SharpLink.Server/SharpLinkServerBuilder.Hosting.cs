namespace SharpLink.Server;

public partial class SharpLinkServerBuilder
{
    /// <summary>Sets an application-owned logger factory only when none was explicitly configured.</summary>
    public void UseLoggerFactoryIfUnset(ILoggerFactory loggerFactory)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(loggerFactory);
            _loggerFactory ??= loggerFactory;
        });
    }

    /// <summary>Uses the Host provider only when no application provider was configured.</summary>
    internal void UseServiceProviderIfUnset(IServiceProvider serviceProvider)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(serviceProvider);
            _serviceProvider ??= serviceProvider;
        });
    }
}
