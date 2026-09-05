using SharpLink.Sdk;

namespace SharpLink.GeneratedAbiMixing;

[RpcContract]
public interface INewGeneratorOldAbstractionsService : IService
{
    [NonCancellable]
    ValueTask<int> AddAsync(int left, int right);
}

[RpcService]
public sealed class NewGeneratorOldAbstractionsService : INewGeneratorOldAbstractionsService
{
    public ValueTask<int> AddAsync(int left, int right) => ValueTask.FromResult(left + right);
}
