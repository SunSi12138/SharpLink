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