using System.Threading.Tasks;
using SharpLink.AotContracts;
using SharpLink.Sdk;

namespace SharpLink.AotServices;

[RpcService]
internal sealed class ReferencedAssemblyService : IReferencedAssemblyService
{
    public ReferencedAssemblyService()
    {
    }

    public ValueTask<string> IdentifyAsync()
        => ValueTask.FromResult("internal-referenced-service");
}
