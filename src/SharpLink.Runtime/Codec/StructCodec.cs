using System.Collections.Immutable;

namespace SharpLink.Runtime;

internal sealed class UnsafeBlitCodec<T> : IRpcCodec<T>
{
    internal static readonly UnsafeBlitCodec<T> Instance = new();
    
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

internal sealed class BlitArrayCodec<T> : IRpcCodec<T[]?> where T:unmanaged
{
    internal static readonly BlitArrayCodec<T> Instance = new();

    static BlitArrayCodec()
    {
        // 安全检查：确保 T 是 unmanaged，否则 AsBytes 行为未定义或不安全
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            throw new InvalidOperationException($"Type {typeof(T)} is not unmanaged/blittable.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in T[]? value, in ArrayBufferWriter<byte> writer)
    {
        if (value is null)
        {
            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(writer.GetSpan(4)), -1);
            writer.Advance(4);
            return;
        }

        var span = writer.GetSpan(4); // 先拿4字节
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(span), value.Length);
        writer.Advance(4);

        if (value.Length <= 0) return;
        
        var byteSpan = MemoryMarshal.AsBytes(new ReadOnlySpan<T>(value));
            
        var dest = writer.GetSpan(byteSpan.Length);
        byteSpan.CopyTo(dest);
        writer.Advance(byteSpan.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T[]? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        if (buffer.FirstSpan.Length < 4) return ReadSlow(buffer);
        
        var length = Unsafe.ReadUnaligned<int>(ref MemoryMarshal.GetReference(buffer.FirstSpan));
        switch (length)
        {
            case -1:
                return null;
            case 0:
                return [];
        }
        
        var elementSize = Unsafe.SizeOf<T>();
        var byteCount = length * elementSize;

        var array = new T[length];
        
        var destBytes = MemoryMarshal.AsBytes(array.AsSpan());

        var payload = buffer.Slice(4);

        if (payload.FirstSpan.Length >= byteCount)
        {
            Unsafe.CopyBlockUnaligned(
                ref MemoryMarshal.GetReference(destBytes),
                ref MemoryMarshal.GetReference(payload.FirstSpan),
                (uint)byteCount);
        }
        else
        {
            payload.Slice(0, byteCount).CopyTo(destBytes);
        }

        return array;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static T[]? ReadSlow(ReadOnlySequence<byte> buffer)
    {
        // 处理 header 分裂的情况 (极罕见)
        Span<byte> header = stackalloc byte[4];
        buffer.Slice(0, 4).CopyTo(header);
        var length = Unsafe.ReadUnaligned<int>(ref MemoryMarshal.GetReference(header));
        
        switch (length)
        {
            case -1:
                return null;
            case 0:
                return [];
        }

        var byteCount = length * Unsafe.SizeOf<T>();
        var array = new T[length];
        buffer.Slice(4, byteCount).CopyTo(MemoryMarshal.AsBytes(array.AsSpan()));
        return array;
    }
}

internal sealed class BlitListCodec<T> : IRpcCodec<List<T>?> where T:unmanaged
{
    internal static readonly BlitListCodec<T> Instance = new();

    static BlitListCodec()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            throw new InvalidOperationException($"Type {typeof(T)} is not unmanaged.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in List<T>? value, in ArrayBufferWriter<byte> writer)
    {
        if (value is null)
        {
            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(writer.GetSpan(4)), -1);
            writer.Advance(4);
            return;
        }

        // 零开销获取 List 内部的 Span
        ReadOnlySpan<T> span = CollectionsMarshal.AsSpan(value);
        
        // 写入长度
        var headerSpan = writer.GetSpan(4);
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(headerSpan), span.Length);
        writer.Advance(4);

        // 写入 Body
        if (span.Length <= 0) return;
        var byteSpan = MemoryMarshal.AsBytes(span);
        var dest = writer.GetSpan(byteSpan.Length);
        byteSpan.CopyTo(dest);
        writer.Advance(byteSpan.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public List<T>? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        // 先反序列化出数组
        var array = BlitArrayCodec<T>.Instance.Deserialize(buffer);
        if (array is null) return null;
        
        // 包装成 List
        // 注意：这会发生一次 T[] 到 List._items 的内存拷贝 (Array.Copy)
        // 但这是目前安全且标准的做法。
        return [..array]; 
    }
}

internal sealed class BlitMemoryCodec<T> : IRpcCodec<Memory<T>>  where T:unmanaged
{
    internal static readonly BlitMemoryCodec<T> Instance = new();

    static BlitMemoryCodec()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            throw new InvalidOperationException($"Type {typeof(T)} is not unmanaged.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in Memory<T> value, in ArrayBufferWriter<byte> writer)
    {
        ReadOnlySpan<T> span = value.Span;
        
        // 写入长度
        var header = writer.GetSpan(4);
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(header), span.Length);
        writer.Advance(4);

        // 写入 Body
        if (span.Length <= 0) return;
        var byteSpan = MemoryMarshal.AsBytes(span);
        var dest = writer.GetSpan(byteSpan.Length);
        byteSpan.CopyTo(dest);
        writer.Advance(byteSpan.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Memory<T> Deserialize(in ReadOnlySequence<byte> buffer)
    {
        var array = BlitArrayCodec<T>.Instance.Deserialize(buffer);
        return array?.AsMemory() ?? Memory<T>.Empty;
    }
}

internal sealed class BlitReadOnlyMemoryCodec<T> : IRpcCodec<ReadOnlyMemory<T>> where T:unmanaged
{
    internal static readonly BlitReadOnlyMemoryCodec<T> Instance = new();
    static BlitReadOnlyMemoryCodec()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            throw new InvalidOperationException($"Type {typeof(T)} is not unmanaged.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in ReadOnlyMemory<T> value, in ArrayBufferWriter<byte> writer)
    {
        var span = value.Span;
        
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(writer.GetSpan(4)), span.Length);
        writer.Advance(4);
        
        if (span.Length <= 0) return;
        var byteSpan = MemoryMarshal.AsBytes(span);
        var dest = writer.GetSpan(byteSpan.Length);
        byteSpan.CopyTo(dest);
        writer.Advance(byteSpan.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlyMemory<T> Deserialize(in ReadOnlySequence<byte> buffer)
    {
        var array = BlitArrayCodec<T>.Instance.Deserialize(buffer);
        return array is null ? ReadOnlyMemory<T>.Empty : new ReadOnlyMemory<T>(array);
    }
}

internal sealed class BlitImmutableArrayCodec<T> : IRpcCodec<ImmutableArray<T>>   where T:unmanaged
{
    internal static readonly BlitImmutableArrayCodec<T> Instance = new();

    static BlitImmutableArrayCodec()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            throw new InvalidOperationException($"Type {typeof(T)} is not unmanaged.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in ImmutableArray<T> value, in ArrayBufferWriter<byte> writer)
    {
        if (value.IsDefault)
        {
            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(writer.GetSpan(4)), -1);
            writer.Advance(4);
            return;
        }

        var span = value.AsSpan();
        
        // Header
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(writer.GetSpan(4)), span.Length);
        writer.Advance(4);

        // Body
        if (span.Length <= 0) return;
        var byteSpan = MemoryMarshal.AsBytes(span);
        var dest = writer.GetSpan(byteSpan.Length);
        byteSpan.CopyTo(dest);
        writer.Advance(byteSpan.Length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ImmutableArray<T> Deserialize(in ReadOnlySequence<byte> buffer)
    {
        var array = BlitArrayCodec<T>.Instance.Deserialize(buffer);
        return array is null ? default : Unsafe.As<T[], ImmutableArray<T>>(ref array);
    }
}