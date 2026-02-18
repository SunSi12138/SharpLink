namespace SharpLink.Runtime;

internal sealed class UnsafeBlitCodec<T> : IRpcCodec<T>
{
    public static readonly UnsafeBlitCodec<T> Instance = new();
    
    static UnsafeBlitCodec()
    {
        // IsReferenceOrContainsReferences 是 JIT Intrinsic，性能极高
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            throw new InvalidOperationException(
                $"Type {typeof(T)} contains references and cannot be used with BlitCodec.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in T value, in ArrayBufferWriter<byte> writer)
    {
        var size = Unsafe.SizeOf<T>(); 
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(writer.GetSpan(size)), value);
        writer.Advance(size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Deserialize(in ReadOnlySequence<byte> buffer)
    {
        var size = Unsafe.SizeOf<T>();
        if (buffer.FirstSpan.Length >= size)
            return Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(buffer.FirstSpan));

        Span<byte> temp = stackalloc byte[size];
        buffer.Slice(0, size).CopyTo(temp);
        return Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(temp));
    }
}

// public sealed class BlitCodec<T> : IRpcCodec<T> where T : unmanaged
// {
//     public static readonly BlitCodec<T> Instance = new();
//
//     [MethodImpl(MethodImplOptions.AggressiveInlining)]
//     public void Serialize(in T value, in ArrayBufferWriter<byte> writer)
//     {
//         // JIT 会把 Unsafe.SizeOf<T>() 编译为常量
//         var size = Unsafe.SizeOf<T>();
//         
//         // 直接写入内存
//         Unsafe.WriteUnaligned(
//             ref MemoryMarshal.GetReference(writer.GetSpan(size)), 
//             value
//         );
//         
//         writer.Advance(size);
//     }
//
//     [MethodImpl(MethodImplOptions.AggressiveInlining)]
//     public T Deserialize(in ReadOnlySequence<byte> buffer)
//     {
//         var size = Unsafe.SizeOf<T>();
//
//         if (buffer.FirstSpan.Length >= size)
//         {
//             return Unsafe.ReadUnaligned<T>(
//                 ref MemoryMarshal.GetReference(buffer.FirstSpan)
//             );
//         }
//
//         Span<byte> temp = stackalloc byte[size];
//         
//         buffer.Slice(0, size).CopyTo(temp);
//         
//         return Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(temp));
//     }
// }
//
//
// public sealed class NullableBlitCodec<T> : IRpcCodec<T?> where T : unmanaged
// {
//     public static readonly NullableBlitCodec<T> Instance = new();
//
//     [MethodImpl(MethodImplOptions.AggressiveInlining)]
//     public void Serialize(in T? value, in ArrayBufferWriter<byte> writer)
//     {
//         var valueSize = Unsafe.SizeOf<T>();
//         var totalSize = valueSize + 1; // 1 byte Tag + Value Size
//
//         ref var start = ref MemoryMarshal.GetReference(writer.GetSpan(totalSize));
//
//         if (value.HasValue)
//         {
//             start = 1; // Tag: HasValue
//             
//             // 偏移 1 字节写入 Value
//             Unsafe.WriteUnaligned(
//                 ref Unsafe.Add(ref start, 1), 
//                 value.GetValueOrDefault()
//             );
//         }
//         else
//         {
//             start = 0; // Tag: Null
//             
//             Unsafe.WriteUnaligned(
//                 ref Unsafe.Add(ref start, 1), 
//                 default(T)
//             );
//         }
//
//         writer.Advance(totalSize);
//     }
//
//     [MethodImpl(MethodImplOptions.AggressiveInlining)]
//     public T? Deserialize(in ReadOnlySequence<byte> buffer)
//     {
//         var valueSize = Unsafe.SizeOf<T>();
//         var totalSize = valueSize + 1;
//
//         if (buffer.FirstSpan.Length >= totalSize)
//         {
//             ref var start = ref MemoryMarshal.GetReference(buffer.FirstSpan);
//             
//             if (start == 0) return null;
//
//             return Unsafe.ReadUnaligned<T>(ref Unsafe.Add(ref start, 1));
//         }
//
//         Span<byte> temp = stackalloc byte[totalSize];
//         buffer.Slice(0, totalSize).CopyTo(temp);
//
//         ref var tempStart = ref MemoryMarshal.GetReference(temp);
//         
//         if (tempStart == 0) return null;
//
//         return Unsafe.ReadUnaligned<T>(ref Unsafe.Add(ref tempStart, 1));
//     }
// }