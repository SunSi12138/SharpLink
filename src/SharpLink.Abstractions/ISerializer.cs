namespace SharpLink.Abstractions;
/// <summary>
/// 序列化抽象
/// </summary>
public interface ISerializer
{
    void Serialize<T>(in T value, IBufferWriter<byte> writer);
    T? Deserialize<T>(ref ReadOnlySequence<byte> sequence);
}