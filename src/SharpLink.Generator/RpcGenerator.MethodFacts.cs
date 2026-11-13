#if SHARPLINK_775_STATIC_SLICE
namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static string MethodFactsExpression(long contractId, RpcMethodModel method)
    {
        var streamCount = method.Parameters.Count(static p => p.IsStream);
        var cancellation = method.HasCancellationToken || method.IsStreamReturn || streamCount != 0;
        var timeout = method.TimeoutTicks is { } ticks ? $"TimeSpan.FromTicks({ticks.ToString(InvariantCulture)}L)" : "null";
        static string Flag(bool value) => value ? "true" : "false";
        return $"new RpcMethodDescriptor({contractId}L, {method.Hash}L, RpcMethodKind.{GetMethodKind(method)}, " +
            $"{Flag(!method.IsOneWay && !method.IsVoid)}, {Flag(streamCount != 0)}, {Flag(method.HasTimeoutAttribute)}, " +
            $"{timeout}, {Flag(method.IsIdempotent)}, {streamCount}, {Flag(method.ResponseNullable)}) " +
            $"{{ SupportsCancellation = {Flag(cancellation)} }}";
    }
}
#endif
