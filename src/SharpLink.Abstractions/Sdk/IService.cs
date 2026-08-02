namespace SharpLink.Sdk;

/// <summary>Marks an interface as eligible to define a SharpLink RPC contract.</summary>
/// <remarks>
/// Contract interfaces must also use <see cref="RpcContractAttribute"/>. The marker prevents
/// unrelated attributed interfaces from entering source generation accidentally.
/// </remarks>
public interface IService
{
}
