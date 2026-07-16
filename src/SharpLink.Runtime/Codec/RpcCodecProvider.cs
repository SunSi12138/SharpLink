using System.Text;

namespace SharpLink.Runtime;

internal sealed class RpcCodecProvider(
    Func<Type, IRpcCodec?>? resolver,
    IReadOnlyDictionary<Type, IRpcCodec> explicitCodecs,
    IReadOnlyDictionary<Type, IRpcGeneratedCodecFactory> generatedFactories) : IRpcCodecProvider
{
    private readonly ConcurrentDictionary<Type, IRpcCodec> _resolvedCodecs = new(explicitCodecs);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IRpcCodec<T> GetCodec<T>()
    {
        // Built-in codecs are immutable process-wide singletons. Resolve them once per
        // closed generic type so primitive response decoding does not pay a Type-keyed
        // dictionary lookup on every RPC.
        if (SharedRpcCodec<T>.Instance is { } shared)
            return shared;

        if (_resolvedCodecs.TryGetValue(typeof(T), out var registered))
            return Cast<T>(registered);

        if (generatedFactories.TryGetValue(typeof(T), out var generatedFactory))
        {
            var generated = generatedFactory.Create(this);
            var selected = _resolvedCodecs.GetOrAdd(typeof(T), generated);
            return Cast<T>(selected);
        }

        var resolved = resolver?.Invoke(typeof(T));
        if (resolved is not null)
        {
            var typed = Cast<T>(resolved);
            _resolvedCodecs.TryAdd(typeof(T), typed);
            return typed;
        }

        if (typeof(T).IsValueType && !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            return UnsafeBlitCodec<T>.Instance;

        throw new NotSupportedException(
            $"Codec for '{typeof(T).FullName}' was not registered in this SharpLink runtime context.");
    }

    private static IRpcCodec<T> Cast<T>(IRpcCodec codec)
        => codec as IRpcCodec<T> ?? throw new InvalidOperationException(
            $"The codec registered for '{typeof(T).FullName}' implements an incompatible codec interface.");
}

internal static class SharedRpcCodec<T>
{
    public static readonly IRpcCodec<T>? Instance = Create();

    private static IRpcCodec<T>? Create()
    {
        if (BuiltinRpcCodecs.TryGet(typeof(T), out var builtin))
            return (IRpcCodec<T>)builtin;
        return null;
    }
}

internal static class BuiltinRpcCodecs
{
    private static readonly IReadOnlyDictionary<Type, IRpcCodec> Codecs = Create();

    public static bool TryGet(Type type, out IRpcCodec codec) => Codecs.TryGetValue(type, out codec!);

    private static IReadOnlyDictionary<Type, IRpcCodec> Create()
    {
        var codecs = new Dictionary<Type, IRpcCodec>();
        Add(codecs, BoolCodec.Instance); Add(codecs, NullableBoolCodec.Instance);
        Add(codecs, ByteCodec.Instance); Add(codecs, NullableByteCodec.Instance);
        Add(codecs, SByteCodec.Instance); Add(codecs, NullableSByteCodec.Instance);
        Add(codecs, Int16Codec.Instance); Add(codecs, NullableInt16Codec.Instance);
        Add(codecs, UInt16Codec.Instance); Add(codecs, NullableUInt16Codec.Instance);
        Add(codecs, CharCodec.Instance); Add(codecs, NullableCharCodec.Instance);
        Add(codecs, HalfCodec.Instance); Add(codecs, NullableHalfCodec.Instance);
        Add(codecs, Int32Codec.Instance); Add(codecs, NullableInt32Codec.Instance);
        Add(codecs, UInt32Codec.Instance); Add(codecs, NullableUInt32Codec.Instance);
        Add(codecs, FloatCodec.Instance); Add(codecs, NullableFloatCodec.Instance);
        Add(codecs, RuneCodec.Instance); Add(codecs, NullableRuneCodec.Instance);
        Add(codecs, IndexCodec.Instance); Add(codecs, NullableIndexCodec.Instance);
        Add(codecs, Int64Codec.Instance); Add(codecs, NullableInt64Codec.Instance);
        Add(codecs, UInt64Codec.Instance); Add(codecs, NullableUInt64Codec.Instance);
        Add(codecs, DoubleCodec.Instance); Add(codecs, NullableDoubleCodec.Instance);
        Add(codecs, RangeCodec.Instance); Add(codecs, NullableRangeCodec.Instance);
        Add(codecs, Int128Codec.Instance); Add(codecs, NullableInt128Codec.Instance);
        Add(codecs, UInt128Codec.Instance); Add(codecs, NullableUInt128Codec.Instance);
        Add(codecs, GuidCodec.Instance); Add(codecs, NullableGuidCodec.Instance);
        Add(codecs, DecimalCodec.Instance); Add(codecs, NullableDecimalCodec.Instance);
        Add(codecs, DateTimeCodec.Instance); Add(codecs, NullableDateTimeCodec.Instance);
        Add(codecs, DateTimeOffsetCodec.Instance); Add(codecs, NullableDateTimeOffsetCodec.Instance);
        Add(codecs, DateOnlyCodec.Instance); Add(codecs, NullableDateOnlyCodec.Instance);
        Add(codecs, TimeOnlyCodec.Instance); Add(codecs, NullableTimeOnlyCodec.Instance);
        Add(codecs, TimeSpanCodec.Instance); Add(codecs, NullableTimeSpanCodec.Instance);
        Add(codecs, StringCodec.Instance);

        AddBlitCollections<bool>(codecs); AddBlitCollections<byte>(codecs);
        AddBlitCollections<sbyte>(codecs); AddBlitCollections<short>(codecs);
        AddBlitCollections<ushort>(codecs); AddBlitCollections<char>(codecs);
        AddBlitCollections<Half>(codecs); AddBlitCollections<int>(codecs);
        AddBlitCollections<uint>(codecs); AddBlitCollections<float>(codecs);
        AddBlitCollections<Rune>(codecs); AddBlitCollections<long>(codecs);
        AddBlitCollections<ulong>(codecs); AddBlitCollections<double>(codecs);
        AddBlitCollections<Guid>(codecs); AddBlitCollections<decimal>(codecs);
        AddBlitCollections<DateTimeOffset>(codecs); AddBlitCollections<DateTime>(codecs);
        AddBlitCollections<DateOnly>(codecs); AddBlitCollections<TimeOnly>(codecs);
        AddBlitCollections<TimeSpan>(codecs); AddBlitCollections<Int128>(codecs);
        AddBlitCollections<UInt128>(codecs); AddBlitCollections<Index>(codecs);
        AddBlitCollections<Range>(codecs);
        return codecs;
    }

    private static void Add<T>(Dictionary<Type, IRpcCodec> codecs, IRpcCodec<T> codec)
        => codecs.Add(typeof(T), codec);

    private static void AddBlitCollections<T>(Dictionary<Type, IRpcCodec> codecs) where T : unmanaged
    {
        Add(codecs, BlitArrayCodec<T>.Instance);
        Add(codecs, BlitImmutableArrayCodec<T>.Instance);
        Add(codecs, BlitListCodec<T>.Instance);
        Add(codecs, BlitMemoryCodec<T>.Instance);
        Add(codecs, BlitReadOnlyMemoryCodec<T>.Instance);
    }
}
