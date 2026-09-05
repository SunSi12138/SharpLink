using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

internal static class SharpLinkClientReadinessSharedSupport
{
    internal static readonly ClientReadinessFacts ReadyFacts = new(
        ActiveEndpoints: 1,
        ReadyEndpoints: 1,
        ReadyConnections: 1,
        TargetReadyEndpoints: 1);

    internal static readonly ClientReadinessFacts NotReadyFacts = new(
        ActiveEndpoints: 1,
        ReadyEndpoints: 0,
        ReadyConnections: 0,
        TargetReadyEndpoints: 1);

    internal static Exception CaptureException(Action action)
    {
        try
        {
            action();
            return new Exception("expected the operation to fail");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    internal static async Task<Exception> CaptureExceptionAsync(Task operation)
    {
        try
        {
            await operation;
            return new Exception("expected the operation to fail");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    internal static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
