using System.Collections.Immutable;

namespace SharpLink.Runtime;
internal sealed class ByteCodec : IRpcCodec<byte>
{
    internal static readonly ByteCodec Instance = new();
    private const int Size = 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in byte value, in ArrayBufferWriter<byte> writer)
    {
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(writer.GetSpan(Size)), value);
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Deserialize(in ReadOnlySequence<byte> buffer)
    {
        return MemoryMarshal.GetReference(buffer.FirstSpan);
    }
}

internal sealed class NullableByteCodec : IRpcCodec<byte?>
{
    internal static readonly NullableByteCodec Instance = new();
    private const int Size = 2; // 1 byte tag + 1 byte value

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in byte? value, in ArrayBufferWriter<byte> writer)
    {
        ref var start = ref MemoryMarshal.GetReference(writer.GetSpan(Size));

        if (value.HasValue)
        {
            start = 1;
            Unsafe.Add(ref start, 1) = value.GetValueOrDefault();
        }
        else
        {
            Unsafe.WriteUnaligned(ref start, (ushort)0);
        }
        
        writer.Advance(Size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        if (buffer.FirstSpan.Length >= Size)
        {
            ref var start = ref MemoryMarshal.GetReference(buffer.FirstSpan);
            if (start == 0) return null;
            return Unsafe.Add(ref start, 1);
        }

        Span<byte> temp = stackalloc byte[Size];
        
        buffer.CopyTo(temp);

        ref var tempStart = ref MemoryMarshal.GetReference(temp);
        if (tempStart == 0) return null;
        
        return Unsafe.Add(ref tempStart, 1);
    }
}
internal sealed class ByteArrayCodec : IRpcCodec<byte[]?>
{
    internal static readonly ByteArrayCodec Instance = new();

    public void Serialize(in byte[]? value, in ArrayBufferWriter<byte> writer)
    {
        if (value == null) { CodecHelpers.WriteInt32(writer, -1); return; }

        var count = value.Length;
        CodecHelpers.WriteInt32(writer, count);

        if (count == 0) return;

        writer.Write(value);
    }

    public byte[]? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        int count;

        if (buffer.FirstSpan.Length >= 4)
        {
            count = BinaryPrimitives.ReadInt32LittleEndian(buffer.FirstSpan);
        }
        else
        {
            Span<byte> lenBytes = stackalloc byte[4];
            buffer.Slice(0, 4).CopyTo(lenBytes);
            count = BinaryPrimitives.ReadInt32LittleEndian(lenBytes);
        }

        switch (count)
        {
            case < 0:
                return null;
            case 0:
                return [];
        }

        var dataSequence = buffer.Slice(4);

        var result = new byte[count];
        
        dataSequence.Slice(0, count).CopyTo(result);
        
        return result;
    }
}


internal sealed class ByteArraySegmentCodec : IRpcCodec<ArraySegment<byte>?>
{
    internal static readonly ByteArraySegmentCodec Instance = new();

    public void Serialize(in ArraySegment<byte>? value, in ArrayBufferWriter<byte> writer)
    {
        if (value?.Array is null) { CodecHelpers.WriteInt32(writer, -1); return; }

        var segment = value.Value;
        var count = segment.Count;
        CodecHelpers.WriteInt32(writer, count);
        if (count == 0) return;
        
        writer.Write(value.Value);
    }

    public ArraySegment<byte>? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        var arr = ByteArrayCodec.Instance.Deserialize(buffer);
        return arr is null ? null! : new ArraySegment<byte>(arr);
    }
}

internal sealed class ByteMemoryCodec : IRpcCodec<Memory<byte>?>
{
    internal static readonly ByteMemoryCodec Instance = new();

    public void Serialize(in Memory<byte>? value, in ArrayBufferWriter<byte> writer)
    {
        if (!value.HasValue) { CodecHelpers.WriteInt32(writer, -1); return; }

        var src = value.Value.Span;
        var count = src.Length;
        CodecHelpers.WriteInt32(writer, count);
        if (count == 0) return;

        writer.Write(src);
    }

    public Memory<byte>? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        var arr = ByteArrayCodec.Instance.Deserialize(buffer);
        return arr;
    }
}


internal sealed class ByteReadOnlyMemoryCodec : IRpcCodec<ReadOnlyMemory<byte>?>
{
    internal static readonly ByteReadOnlyMemoryCodec Instance = new();

    public void Serialize(in ReadOnlyMemory<byte>? value, in ArrayBufferWriter<byte> writer)
    {
        if (!value.HasValue) { CodecHelpers.WriteInt32(writer, -1); return; }

        var src = value.Value.Span;
        var count = src.Length;
        CodecHelpers.WriteInt32(writer, count);
        if (count == 0) return;

        var dst = writer.GetSpan(count);
        src.CopyTo(dst);
        writer.Advance(count);
    }

    public ReadOnlyMemory<byte>? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        var arr = ByteArrayCodec.Instance.Deserialize(buffer);
        return arr;
    }
}

internal sealed class ByteListCodec : IRpcCodec<List<byte>?>
{
    internal static readonly ByteListCodec Instance = new();

    public void Serialize(in List<byte>? value, in ArrayBufferWriter<byte> writer)
    {
        if (value == null) { CodecHelpers.WriteInt32(writer, -1); return; }

        var count = value.Count;
        CodecHelpers.WriteInt32(writer, count);
        if (count == 0) return;

        var src = CollectionsMarshal.AsSpan(value);
        writer.Write(src);
    }

    public List<byte>? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        // 读取长度逻辑同上
        int count;
        
        if (buffer.FirstSpan.Length >= 4)
        {
            count = BinaryPrimitives.ReadInt32LittleEndian(buffer.FirstSpan);
        }
        else
        {
            Span<byte> lenBytes = stackalloc byte[4];
            buffer.Slice(0, 4).CopyTo(lenBytes);
            count = BinaryPrimitives.ReadInt32LittleEndian(lenBytes);
        }

        switch (count)
        {
            case < 0:
                return null;
            case 0:
                return [];
        }
        var list = new List<byte>(count);
        CollectionsMarshal.SetCount(list, count); 
        var listSpan = CollectionsMarshal.AsSpan(list);
        buffer.Slice(4, count).CopyTo(listSpan);
        return list;
    }
}

internal sealed class ByteImmutableArrayCodec : IRpcCodec<ImmutableArray<byte>?>
{
    internal static readonly ByteImmutableArrayCodec Instance = new();

    public void Serialize(in ImmutableArray<byte>? value, in ArrayBufferWriter<byte> writer)
    {
        if (!value.HasValue) { CodecHelpers.WriteInt32(writer, -1); return; }

        var src = value.Value.AsSpan();
        var count = src.Length;
        CodecHelpers.WriteInt32(writer, count);
        if (count == 0) return;

        writer.Write(src);
    }

    public ImmutableArray<byte>? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        // 1. 解析长度 (复用之前的逻辑)
        int count;

        if (buffer.FirstSpan.Length >= 4)
        {
            count = BinaryPrimitives.ReadInt32LittleEndian(buffer.FirstSpan);
        }
        else
        {
            Span<byte> lenBytes = stackalloc byte[4];
            buffer.Slice(0, 4).CopyTo(lenBytes);
            count = BinaryPrimitives.ReadInt32LittleEndian(lenBytes);
        }

        switch (count)
        {
            // 2. 处理特殊情况
            case < 0:
                return null;
            case 0:
                return ImmutableArray<byte>.Empty;
        }
        var internalArray = new byte[count];
        buffer.Slice(4, count).CopyTo(internalArray);
        return Unsafe.As<byte[], ImmutableArray<byte>>(ref internalArray);
    }
}

