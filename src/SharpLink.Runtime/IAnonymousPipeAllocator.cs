namespace SharpLink.Runtime;
/// <summary>
/// 用于向Host中的其他服务暴露，来重复建立多个匿名管道连接
/// </summary>
public interface IAnonymousPipeAllocator
{
    (string InHandle,string OutHandle) AllocateNewSession();
}