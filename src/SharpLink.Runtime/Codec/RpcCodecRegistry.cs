using System.Text;

namespace SharpLink.Runtime;

public static class RpcCodecRegistry
{
    private static Func<Type,IRpcCodec?>? _codecResolver;

    static RpcCodecRegistry()
    {
        RegisterBuiltinCodec();
        RegisterBuiltinArrayCodec();
    }
    
    [Obsolete("Register codecs with SharpClientBuilder.UseCodec or SharpLinkServerBuilder.UseCodec.")]
    public static void Register<T>(IRpcCodec<T> codec)
        => RegisterCore(codec);

    private static void RegisterCore<T>(IRpcCodec<T> codec)
    {
        if(RpcCodec<T>.Initialized)
            throw new Exception($"RpcCodecRegistry duplicated register type {typeof(T)}");
        RpcCodec<T>.Codec = codec;
    }
    public static void Initialize(){}
    [Obsolete("Configure a codec resolver with the client or server builder UseSerializer method.")]
    public static void Initialize(Func<Type,IRpcCodec?>? codecResolver) => _codecResolver = codecResolver;
    internal static IRpcCodec<T> Create<T>()
    {
        if (typeof(T).IsValueType && !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            return UnsafeBlitCodec<T>.Instance;
        }
        

        //JIT模式使用Resolver
        var codec =  _codecResolver?.Invoke(typeof(T));
        if(codec != null) return (IRpcCodec<T>)codec;
        
        //AOT 模式：必须手动注册
        throw new NotSupportedException(
            $"Codec for '{typeof(T).Name}' not found. " +
            $"In NativeAOT, you must explicitly register types using RpcCodecRegistry.Register<T>().");
    }


    private static void RegisterBuiltinCodec()
    {
        RegisterCore(BoolCodec.Instance);           RegisterCore(NullableBoolCodec.Instance);
        RegisterCore(ByteCodec.Instance);           RegisterCore(NullableByteCodec.Instance);
        RegisterCore(SByteCodec.Instance);          RegisterCore(NullableSByteCodec.Instance);
        
        RegisterCore(Int16Codec.Instance);          RegisterCore(NullableInt16Codec.Instance);
        RegisterCore(UInt16Codec.Instance);         RegisterCore(NullableUInt16Codec.Instance);
        RegisterCore(CharCodec.Instance);           RegisterCore(NullableCharCodec.Instance);
        RegisterCore(HalfCodec.Instance);           RegisterCore(NullableHalfCodec.Instance);
        
        RegisterCore(Int32Codec.Instance);          RegisterCore(NullableInt32Codec.Instance);
        RegisterCore(UInt32Codec.Instance);         RegisterCore(NullableUInt32Codec.Instance);
        RegisterCore(FloatCodec.Instance);          RegisterCore(NullableFloatCodec.Instance);
        RegisterCore(RuneCodec.Instance);           RegisterCore(NullableRuneCodec.Instance);
        RegisterCore(IndexCodec.Instance);          RegisterCore(NullableIndexCodec.Instance);
        
        RegisterCore(Int64Codec.Instance);          RegisterCore(NullableInt64Codec.Instance);
        RegisterCore(UInt64Codec.Instance);         RegisterCore(NullableUInt64Codec.Instance);
        RegisterCore(DoubleCodec.Instance);         RegisterCore(NullableDoubleCodec.Instance);
        RegisterCore(RangeCodec.Instance);          RegisterCore(NullableRangeCodec.Instance);
        
        RegisterCore(Int128Codec.Instance);         RegisterCore(NullableInt128Codec.Instance);
        RegisterCore(UInt128Codec.Instance);        RegisterCore(NullableUInt128Codec.Instance);
        RegisterCore(GuidCodec.Instance);           RegisterCore(NullableGuidCodec.Instance);
        RegisterCore(DecimalCodec.Instance);        RegisterCore(NullableDecimalCodec.Instance);
        
        RegisterCore(DateTimeCodec.Instance);       RegisterCore(NullableDateTimeCodec.Instance);
        RegisterCore(DateTimeOffsetCodec.Instance); RegisterCore(NullableDateTimeOffsetCodec.Instance);
        RegisterCore(DateOnlyCodec.Instance);       RegisterCore(NullableDateOnlyCodec.Instance);
        RegisterCore(TimeOnlyCodec.Instance);       RegisterCore(NullableTimeOnlyCodec.Instance);
        RegisterCore(TimeSpanCodec.Instance);       RegisterCore(NullableTimeSpanCodec.Instance);
        
        RegisterCore(StringCodec.Instance);
    }

    private static void RegisterBuiltinArrayCodec()
    {
        RegisterBlitArrayCore<bool>();             RegisterBlitArrayCore<byte>();
        RegisterBlitArrayCore<sbyte>();            RegisterBlitArrayCore<short>();
        RegisterBlitArrayCore<ushort>();           RegisterBlitArrayCore<char>();
        RegisterBlitArrayCore<Half>();             RegisterBlitArrayCore<int>();
        RegisterBlitArrayCore<uint>();             RegisterBlitArrayCore<float>();
        RegisterBlitArrayCore<Rune>();             RegisterBlitArrayCore<long>();
        RegisterBlitArrayCore<ulong>();            RegisterBlitArrayCore<double>();
        RegisterBlitArrayCore<Guid>();             RegisterBlitArrayCore<decimal>();
        RegisterBlitArrayCore<DateTimeOffset>();   RegisterBlitArrayCore<DateTime>();
        RegisterBlitArrayCore<DateOnly>();         RegisterBlitArrayCore<TimeOnly>();
        RegisterBlitArrayCore<TimeSpan>();
        RegisterBlitArrayCore<Int128>();           RegisterBlitArrayCore<UInt128>();
        RegisterBlitArrayCore<Index>();            RegisterBlitArrayCore<Range>();
    }

    [Obsolete("Built-in blittable collection codecs are included by every runtime context.")]
    public static void RegisterBlitArray<T>() where T : unmanaged
        => RegisterBlitArrayCore<T>();

    private static void RegisterBlitArrayCore<T>() where T : unmanaged
    {
        RegisterCore(BlitArrayCodec<T>.Instance);
        RegisterCore(BlitImmutableArrayCodec<T>.Instance);
        RegisterCore(BlitListCodec<T>.Instance);
        RegisterCore(BlitMemoryCodec<T>.Instance);
        RegisterCore(BlitReadOnlyMemoryCodec<T>.Instance);
    }
}
