namespace SharpLink.Abstractions;

[Flags]
public enum PacketFlags : byte
{
    None          = 0,
    IsError       = 1<<0,   // 0 = NoError,   1 = Error
    IsCancellable = 1<<1,   // 1 = 客户端允许取消该请求
    IsOneWay      = 1<<2,   // 0 = TowWay,    1 = OneWay
    HasReturn     = 1<<3,   // 1 = RpcResponse payload contains serialized return value
}
