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
    
    public static void Register<T>(IRpcCodec<T> codec)
    {
        if(RpcCodec<T>.Initialized)
            throw new Exception($"RpcCodecRegistry duplicated register type {typeof(T)}");
        RpcCodec<T>.Codec = codec;
    }
    public static void Initialize(){}
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
        Register(BoolCodec.Instance);           Register(NullableBoolCodec.Instance);      
        Register(ByteCodec.Instance);           Register(NullableByteCodec.Instance);
        Register(SByteCodec.Instance);          Register(NullableSByteCodec.Instance);
        
        Register(Int16Codec.Instance);          Register(NullableInt16Codec.Instance);     
        Register(UInt16Codec.Instance);         Register(NullableUInt16Codec.Instance);
        Register(CharCodec.Instance);           Register(NullableCharCodec.Instance);      
        Register(HalfCodec.Instance);           Register(NullableHalfCodec.Instance);
        
        Register(Int32Codec.Instance);          Register(NullableInt32Codec.Instance);     
        Register(UInt32Codec.Instance);         Register(NullableUInt32Codec.Instance);
        Register(FloatCodec.Instance);          Register(NullableFloatCodec.Instance);
        Register(RuneCodec.Instance);           Register(NullableRuneCodec.Instance);
        Register(IndexCodec.Instance);          Register(NullableIndexCodec.Instance); 
        
        Register(Int64Codec.Instance);          Register(NullableInt64Codec.Instance);     
        Register(UInt64Codec.Instance);         Register(NullableUInt64Codec.Instance);
        Register(DoubleCodec.Instance);         Register(NullableDoubleCodec.Instance);    
        Register(RangeCodec.Instance);          Register(NullableRangeCodec.Instance);
        
        Register(Int128Codec.Instance);         Register(NullableInt128Codec.Instance);
        Register(UInt128Codec.Instance);        Register(NullableUInt128Codec.Instance);
        Register(GuidCodec.Instance);           Register(NullableGuidCodec.Instance);
        Register(DecimalCodec.Instance);        Register(NullableDecimalCodec.Instance);
        
        Register(DateTimeCodec.Instance);       Register(NullableDateTimeCodec.Instance);  
        Register(DateTimeOffsetCodec.Instance); Register(NullableDateTimeOffsetCodec.Instance);
        Register(DateOnlyCodec.Instance);       Register(NullableDateOnlyCodec.Instance);
        Register(TimeOnlyCodec.Instance);       Register(NullableTimeOnlyCodec.Instance);
        Register(TimeSpanCodec.Instance);       Register(NullableTimeSpanCodec.Instance);
        
        Register(StringCodec.Instance);
    }

    private static void RegisterBuiltinArrayCodec()
    {
        RegisterBlitArray<bool>();             RegisterBlitArray<byte>();
        RegisterBlitArray<sbyte>();            RegisterBlitArray<short>();
        RegisterBlitArray<ushort>();           RegisterBlitArray<char>();
        RegisterBlitArray<Half>();             RegisterBlitArray<int>();
        RegisterBlitArray<uint>();             RegisterBlitArray<float>();
        RegisterBlitArray<Rune>();             RegisterBlitArray<long>();
        RegisterBlitArray<ulong>();            RegisterBlitArray<double>();
        RegisterBlitArray<Guid>();             RegisterBlitArray<decimal>();
        RegisterBlitArray<DateTimeOffset>();   RegisterBlitArray<DateTime>();         
        RegisterBlitArray<DateOnly>();         RegisterBlitArray<TimeOnly>();
        RegisterBlitArray<TimeSpan>(); 
        RegisterBlitArray<Int128>();           RegisterBlitArray<UInt128>(); 
        RegisterBlitArray<Index>();            RegisterBlitArray<Range>();
    }

    public static void RegisterBlitArray<T>() where T : unmanaged
    {
        Register(BlitArrayCodec<T>.Instance);
        Register(BlitImmutableArrayCodec<T>.Instance);
        Register(BlitListCodec<T>.Instance);
        Register(BlitMemoryCodec<T>.Instance);
        Register(BlitReadOnlyMemoryCodec<T>.Instance);
    }
}