using SharpLink.Sdk;

namespace QuickStart.Contracts;

[RpcContract]
public interface IGreetingService : IService
{
    ValueTask<GreetingReply> GreetAsync(
        GreetingRequest request,
        CancellationToken cancellationToken);
}

public sealed class GreetingRequest
{
    public string Name { get; init; } = string.Empty;
}

public sealed class GreetingReply
{
    public string Message { get; init; } = string.Empty;
}
