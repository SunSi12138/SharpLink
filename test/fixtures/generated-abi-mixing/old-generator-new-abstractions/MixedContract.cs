using SharpLink.Sdk;

namespace SharpLink.GeneratedAbiMixing;

[RpcContract]
public interface IOldGeneratorNewAbstractionsService : IService
{
    [NonCancellable]
    ValueTask<int> AddAsync(int left, int right);
}

[RpcService]
public sealed class OldGeneratorNewAbstractionsService : IOldGeneratorNewAbstractionsService
{
    public ValueTask<int> AddAsync(int left, int right) => ValueTask.FromResult(left + right);
}
