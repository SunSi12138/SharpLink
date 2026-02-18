namespace SharpLink.Abstractions;

//包头辅助解析结构
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct PacketHeader(PacketType Type, PacketFlags Flags, long RequestId);