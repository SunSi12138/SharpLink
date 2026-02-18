namespace SharpLink.Runtime;

public static class RpcCodecRegistry
{
    private static Func<Type,IRpcCodec?>? _codecResolver;

    static RpcCodecRegistry()
    {
        RegisterBuiltinCodec();
    }
    
    public static void Register<T>(IRpcCodec<T> codec)
    {
        if(RpcCodec<T>.Initialized)
            throw new Exception($"RpcCodecRegistry duplicated register type {typeof(T)}");
        RpcCodec<T>.Codec = codec;
    }
    public static void Initialize(){}
    public static void Initialize(Func<Type,IRpcCodec?> codecResolver) => _codecResolver = codecResolver;
    internal static IRpcCodec<T> Create<T>()
    {
        if (typeof(T).IsValueType && !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            return UnsafeBlitCodec<T>.Instance;
        }
        
        
        if (_codecResolver is null)
            throw new InvalidOperationException($"No codec registered for {typeof(T)} and no DefaultCodecResolver set.");
        
        var codec =  _codecResolver(typeof(T));
        
        if(codec is null)
            throw new NotSupportedException($"No codec found for type {typeof(T)}");
        
        return codec as IRpcCodec<T> ?? throw new InvalidOperationException($"Default provider returned wrong type for {typeof(T)}");
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
}