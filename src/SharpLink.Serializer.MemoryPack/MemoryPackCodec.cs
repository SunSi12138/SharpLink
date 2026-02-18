using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using MemoryPack;
using SharpLink.Abstractions;

namespace SharpLink.Runtime;

public abstract class MemoryPackCodec
{   
    // ❌ AOT 環境
    public static Func<Type,IRpcCodec?>? Resolver=>RuntimeFeature.IsDynamicCodeSupported?Resolve:null;
    
    private static IRpcCodec Resolve(Type type)
    {
        var codecType = typeof(MemoryPackCodec<>).MakeGenericType(type);
        var instanceField = codecType.GetField("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        return (IRpcCodec)instanceField!.GetValue(null)!;
    }
}

/// <summary>
/// Serializer 兜底适配
/// </summary>
/// <typeparam name="T"></typeparam>
public sealed class MemoryPackCodec<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]T> : IRpcCodec<T>
{
    public static readonly MemoryPackCodec<T> Instance = new();
    private MemoryPackCodec(){}
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Serialize(in T value, in ArrayBufferWriter<byte> writer)=>MemoryPackSerializer.Serialize(writer, value);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T? Deserialize(in ReadOnlySequence<byte> sequence)=>MemoryPackSerializer.Deserialize<T>(in sequence);
}