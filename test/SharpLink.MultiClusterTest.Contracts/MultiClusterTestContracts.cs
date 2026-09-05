using SharpLink.Sdk;

namespace SharpLink.MultiClusterTest.Contracts;

[RpcContract]
public interface IOrdersContract : IService;
[RpcContract]
public interface IUnroutedContract : IService;
