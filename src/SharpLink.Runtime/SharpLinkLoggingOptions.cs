namespace SharpLink.Runtime;

public sealed class SharpLinkLoggingOptions
{
    public ILoggerFactory LoggerFactory { get; private set; } = NullLoggerFactory.Instance;
    public LogLevel MinimumLogLevel { get; private set; } = LogLevel.Warning;

    public SharpLinkLoggingOptions UseLoggerFactory(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        LoggerFactory = loggerFactory;
        return this;
    }

    public SharpLinkLoggingOptions UseLogger(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        LoggerFactory = new SingleLoggerFactory(logger);
        return this;
    }

    public SharpLinkLoggingOptions UseMinimumLogLevel(LogLevel minimumLogLevel)
    {
        MinimumLogLevel = minimumLogLevel;
        return this;
    }

    public void UseLoggerFactoryIfUnset(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        if (!ReferenceEquals(LoggerFactory, NullLoggerFactory.Instance))
            return;

        LoggerFactory = loggerFactory;
    }
}

internal sealed class SingleLoggerFactory(ILogger logger) : ILoggerFactory
{
    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException("SingleLoggerFactory does not support adding providers.");
    public ILogger CreateLogger(string categoryName) => logger;
    public void Dispose() { }
}
