namespace SharpLink.Client;

internal sealed partial class SharpLinkMultiClusterClient
{
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
        => LogMutationStageCore(
            logger,
            operation,
            clusterKey,
            stage,
            result,
            failureStage,
            configuredConnectionBudget,
            elapsedMilliseconds);

    private static void RecordMutation(string operation, string result, TimeSpan duration)
        => SharpLinkTelemetry.RecordMultiClusterMutation(operation, result, duration);
}
