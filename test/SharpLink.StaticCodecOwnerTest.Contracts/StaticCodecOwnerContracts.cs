using SharpLink.Sdk;

namespace SharpLink.StaticCodecOwnerTest.Contracts;

public interface IContractA : IService;
public interface IContractB : IService;
public sealed class ContractAService : IContractA;
public sealed class ContractBService : IContractB;
public readonly record struct SharedPayload(int Value);
