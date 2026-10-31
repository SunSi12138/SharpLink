using Microsoft.Extensions.Logging;

namespace Inventory.LogProbe;

public static partial class First
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "{A}/{B}/{C}/{D}/{E}/{F}/{G}")]
    public static partial void LogFirst(ILogger logger, int a, int b, int c, int d, int e, int f, int g);
}

public static partial class Second
{
    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "{A}/{B}/{C}/{D}/{E}/{F}/{G}")]
    public static partial void LogSecond(ILogger logger, int a, int b, int c, int d, int e, int f, int g);
}
