namespace SharpLink.Abstractions;

public static class ProtocolConstants
{
    
    public const byte MagicNumber = 0x88;//88
    //使用几个字节
    // 固定头部长度标识 (15字节[MagicNumber 1][PacketLength 4][PacketType 1][PacketFlags 1] [RequestId 8])
    public const int HeaderBytes = 15;
    public const int MagicNumberOffset = 0;
    public const int PacketLengthOffset = 1;
    public static readonly Range PacketLengthRange = 1..5;
    public const int PacketTypeOffset  =5;
    public const int PacketFlagsOffset  =6;
    public static readonly Range PacketRequestIdRange = 7..15;
    // 固定请求头部长度 （16字节[ServiceHash/InterfaceHash 8][MethodHash 8]
    public const int RequestHeaderLength  =16;
    public const int PacketLengthBytes=4;   //包体负载长度
    public const int RequestIdLengthBytes=8;
    public const int ServiceIdLengthBytes=8;
    public const int MethodIdLengthBytes=8;
    public const int ActorIdLengthBytes=8;
    
}
//包头辅助解析结构
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct PacketHeader(PacketType Type, PacketFlags Flags, long RequestId);
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly record struct RequestHeader(long ServiceId, long MethodId, long ActorId);

/// <summary>
/// 传输层动作
/// </summary>
public enum PacketType : byte
{
    Error           = 0,   //错误
    Handshake       = 1,   //握手
    Heartbeat       = 2,   //客户端心跳
    RpcCall         = 3,   //远程调用
    RpcResponse     = 4,
    DisConnect      = 5,   //主动断开连接
    Cancel          = 6,   //取消请求

    // --- 流式扩展 ---
    StreamChunk     = 10,  // 流数据块
    StreamComplete  = 11,  // 流结束信号
    StreamError     = 12,  // 流异常
}
[Flags]
public enum PacketFlags : byte
{
    None          = 0,
    IsCancellable = 1<<0,   // 1 = 客户端允许取消该请求
    IsError       = 1<<1,   // 0 = NoError,   1 = Error
    IsOneWay      = 1<<2,   // 0 = TowWay,    1 = OneWay
}
