namespace SharpLink.Client;

internal sealed partial class SharpLinkMultiClusterClient
{
    [LoggerMessage(
        EventId = LogEvents.Client.BackgroundLoopUnhandledException,
        Level = LogLevel.Error,
        Message = "Multi-cluster framework task {Operation} terminated with an unhandled exception.")]
    private static partial void LogMultiClusterFrameworkTaskFailure(
        ILogger logger,
        string operation,
        Exception exception);

    [LoggerMessage(
        EventId = LogEvents.Client.MultiClusterMutationStage,
        Level = LogLevel.Information,
        Message = "Multi-cluster {Operation} for {ClusterKey} reached {Stage} with {Result}; failure stage {FailureStage}; configured budget {ConfiguredConnectionBudget}; elapsed {ElapsedMilliseconds} ms.")]
    private static partial void LogMutationStageCore(
        ILogger logger,
        string operation,
        string clusterKey,
        string stage,
        string result,
        string? failureStage,
        int configuredConnectionBudget,
        double elapsedMilliseconds);

    private static void LogMutationStage(
        ILogger logger,
        string operation,
        string clusterKey,
        string stage,
        string result,
        int configuredConnectionBudget,
        double elapsedMilliseconds,
        string? failureStage = null)
    {
        try
        {
            LogMutationStageCore(
                logger,
                operation,
                clusterKey,
                stage,
                result,
                failureStage,
                configuredConnectionBudget,
                elapsedMilliseconds);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // Observability providers are application-owned and must not control mutation ownership.
        }
    }

    private static void RecordMutation(string operation, string result, TimeSpan duration)
    {
        try
        {
            SharpLinkTelemetry.RecordMultiClusterMutation(operation, result, duration);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // A MeterListener callback must not change a committed lifecycle result.
        }
    }
}
