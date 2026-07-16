namespace SharpLink.Runtime;



public static class RpcCodec
{
    static RpcCodec()
    {
        RpcCodecRegistry.Initialize();
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Serialize<T>(in T value, IBufferWriter<byte> buffer)=>RpcCodec<T>.Codec.Serialize(value, buffer);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T? Deserialize<T>(in ReadOnlySequence<byte> buffer)=>RpcCodec<T>.Codec.Deserialize(buffer);

}

internal static class RpcCodec<T>
{
    // ReSharper disable once StaticMemberInGenericType
    internal static bool Initialized {get; private set;}
    internal static IRpcCodec<T> Codec
    {
        get=>field??=RpcCodecRegistry.Create<T>();
        set
        {
            field = value;
            Initialized = true;
        }
    }
}
