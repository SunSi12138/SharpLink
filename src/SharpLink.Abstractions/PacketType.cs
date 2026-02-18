namespace SharpLink.Abstractions;

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