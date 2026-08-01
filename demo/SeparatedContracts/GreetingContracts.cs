using SharpPack;
using SharpLink.Sdk;

namespace SeparatedContracts;

[RpcContract]
public interface IGreetingService : IService
{
    [NonCancellable]
    ValueTask<string> Greet(GreetRequest request);
    [NonCancellable]
    ValueTask<int> Add(int left, int right);
}

[SharpPackable]
public partial class GreetRequest
{
    public string Name { get; set; } = string.Empty;
    public int Repeat { get; set; }
}
