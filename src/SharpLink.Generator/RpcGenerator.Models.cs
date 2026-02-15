namespace SharpLink.Generator;

internal record RpcServiceModel(string ServiceName, string ServiceNamespace, string ServiceFullName, RpcInterfaceModel Interface);

internal record RpcInterfaceModel(
    string Name,
    string Namespace,
    string FullName,
    long Hash,
    EquatableArray<RpcMethodModel> Methods);

internal record RpcMethodModel(
    string Name,
    string ReturnType,
    bool IsGenericTask,
    bool IsStreamReturn,
    string? StreamItemType,
    string? GenericArgumentType,
    bool IsVoid,
    bool IsOneWay,
    bool HasCancellationToken,
    bool HasTimeoutAttribute,
    double? TimeoutSeconds,
    long Hash,
    EquatableArray<RpcParameterModel> Parameters);

internal record RpcParameterModel(
    string Name,
    string Type,
    bool IsStream,
    string? StreamItemType,
    bool IsBlittable,
    bool IsValueType,
    bool IsNullableReference,
    bool IsCancellationToken);

internal readonly record struct InvalidRpcMethodModel(string MethodName, string ReturnType, Location? Location);
internal readonly record struct InvalidCancellationTokenMethodModel(string MethodName, Location? Location);
internal readonly record struct InvalidStreamCountMethodModel(string MethodName, int StreamParameterCount, Location? Location);
internal readonly record struct InvalidTimeoutCancellationMethodModel(string MethodName, Location? Location);

internal static class Hashing
{
    private const ulong FnvPrime = 1099511628211;
    private const ulong FnvOffsetBasis = 14695981039346656037;

    public static long GetMethodHash(string mName, string[] pNames)
    {
        var cleanP = string.Join(",", pNames).Replace("global::", "").Replace(" ", "");
        return (long)Hash($"{mName}({cleanP})");
    }

    public static long GetInterfaceHash(string iName)
    {
        return (long)Hash(iName.Replace("global::", "").Replace(" ", ""));
    }

    private static ulong Hash(string s)
    {
        ulong hash = FnvOffsetBasis;
        foreach (var c in s)
        {
            hash ^= c;
            hash *= FnvPrime;
        }
        return hash;
    }
}
