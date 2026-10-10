using SharpLink.Abstractions;
using SharpLink.Sdk;

namespace Issue739Net11;

// Independent Add-only pilot fixture; identical source in both lowering variants.
[RpcContract]
public interface ITiny : IService
{
    [NonCancellable] ValueTask<int> AddAsync(int left, int right);
}

[RpcService]
public sealed class TinyService : ITiny
{
    private long _received;
    public long Received => Interlocked.Read(ref _received);
    public ValueTask<int> AddAsync(int left, int right)
    {
        Interlocked.Increment(ref _received);
        return ValueTask.FromResult(left + right);
    }
}
