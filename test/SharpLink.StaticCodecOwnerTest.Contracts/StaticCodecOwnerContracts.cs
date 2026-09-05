using SharpLink.Sdk;

namespace SharpLink.StaticCodecOwnerTest.Contracts;

[RpcContract]
public interface IContractA : IService;
[RpcContract]
public interface IContractB : IService;
public sealed class ContractAService : IContractA;
public sealed class ContractBService : IContractB;
public readonly record struct SharedPayload(int Value);
