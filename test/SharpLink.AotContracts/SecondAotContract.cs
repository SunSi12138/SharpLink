using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Sdk;

namespace SharpLink.AotContracts;

[RpcContract]
public interface ISecondAotService : IService
{
    [NonCancellable]
    ValueTask<int> MultiplyAsync(int value);
}
