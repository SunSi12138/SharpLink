namespace SharpLink.Server;

// 用于存储 "接口 Hash" 到 "具体服务类型/Stub" 的映射
public class RpcServiceRegistry
{
    private readonly Dictionary<long, ServiceEntry> _entries = new();

    public void Register(long hash, IRpcStub stub, Type serviceType,IServiceProvider sp)
    {
        _entries[hash] =new ServiceEntry(stub, serviceType, sp);
    }

    public bool TryGetEntry(long hash, out ServiceEntry? entry)=>_entries.TryGetValue(hash, out entry);
}