namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static void AppendCollectionCodec(StringBuilder sb, GeneratedCodecModel model)
    {
        sb.AppendLine($"internal sealed class {model.CodecName} : IRpcCodec<{model.TypeName}>");
        sb.AppendLine("{");
        if (model.Kind == GeneratedCodecKind.Dictionary)
        {
            sb.AppendLine($"    private readonly IRpcCodec<{model.KeyType}> __keyCodec;");
            sb.AppendLine($"    private readonly IRpcCodec<{model.ValueType}> __valueCodec;");
        }
        else
        {
            sb.AppendLine($"    private readonly IRpcCodec<{model.ElementType}> __elementCodec;");
        }
        sb.AppendLine();
        sb.AppendLine($"    internal {model.CodecName}(IRpcCodecProvider provider)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(provider);");
        if (model.Kind == GeneratedCodecKind.Dictionary)
        {
            sb.AppendLine($"        __keyCodec = provider.GetCodec<{model.KeyType}>();");
            sb.AppendLine($"        __valueCodec = provider.GetCodec<{model.ValueType}>();");
        }
        else
        {
            sb.AppendLine($"        __elementCodec = provider.GetCodec<{model.ElementType}>();");
        }
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine($"    public void Serialize(in {model.TypeName} value, IBufferWriter<byte> writer)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(writer);");
        sb.AppendLine("        var rpcWriter = writer as IRpcByteBufferWriter ?? throw new InvalidOperationException(\"Generated collection Codecs require the SharpLink packet writer.\");");
        AppendCollectionWrite(sb, model);
        sb.AppendLine("    }");
        sb.AppendLine();
        var returnType = model.IsReferenceType ? model.TypeName + "?" : model.TypeName;
        sb.AppendLine($"    public {returnType} Deserialize(in ReadOnlySequence<byte> buffer)");
        sb.AppendLine("    {");
        AppendCollectionRead(sb, model);
        sb.AppendLine("    }");
        AppendFactory(sb, model);
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void AppendCollectionWrite(StringBuilder sb, GeneratedCodecModel model)
    {
        if (model.Kind == GeneratedCodecKind.Nullable)
        {
            sb.AppendLine("        if (!value.HasValue)");
            sb.AppendLine("        {");
            sb.AppendLine("            RpcGeneratedCodecWire.WritePresence(writer, false);");
            sb.AppendLine("            return;");
            sb.AppendLine("        }");
            sb.AppendLine("        RpcGeneratedCodecWire.WritePresence(writer, true);");
            sb.AppendLine("        __elementCodec.Serialize(value.Value, writer);");
            return;
        }

        var nullCondition = model.Kind switch
        {
            GeneratedCodecKind.Array or GeneratedCodecKind.List or GeneratedCodecKind.Dictionary => "value is null",
            GeneratedCodecKind.ImmutableArray => "value.IsDefault",
            _ => null
        };
        if (nullCondition is not null)
        {
            sb.AppendLine($"        if ({nullCondition})");
            sb.AppendLine("        {");
            sb.AppendLine("            RpcGeneratedCodecWire.WriteCollectionCount(writer, 0, true);");
            sb.AppendLine("            return;");
            sb.AppendLine("        }");
        }
        var countExpression = model.Kind == GeneratedCodecKind.Dictionary ? "value.Count" : "value.Length";
        if (model.Kind == GeneratedCodecKind.List)
            countExpression = "value.Count";
        var itemExpression = model.Kind is GeneratedCodecKind.Memory or GeneratedCodecKind.ReadOnlyMemory
            ? "value.Span[index]"
            : "value[index]";
        if (model.ElementIsString && model.Kind != GeneratedCodecKind.Dictionary)
        {
            sb.AppendLine($"        if ((uint){countExpression} > RpcGeneratedCodecWire.MaximumCollectionItems)");
            sb.AppendLine("            throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted, $\"Generated collection contains more than {RpcGeneratedCodecWire.MaximumCollectionItems} items.\");");
            sb.AppendLine($"        var __countMarker = checked((uint){countExpression} + 1U);");
            sb.AppendLine("        var __encodedSize = 1;");
            sb.AppendLine("        while (__countMarker >= 0x80)");
            sb.AppendLine("        {");
            sb.AppendLine("            __encodedSize++;");
            sb.AppendLine("            __countMarker >>= 7;");
            sb.AppendLine("        }");
            sb.AppendLine($"        for (var __index = 0; __index < {countExpression}; __index++)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var __item = {itemExpression.Replace("index", "__index")};");
            sb.AppendLine("            if (__item is not null && __item.Length > (RpcGeneratedCodecWire.MaximumStringPayloadBytes / 2))");
            sb.AppendLine("                throw new ArgumentOutOfRangeException(nameof(__item), \"Serialized payload exceeds the protocol maximum.\");");
            sb.AppendLine("            __encodedSize = checked(__encodedSize + sizeof(uint) + sizeof(uint) + (__item is null ? 0 : __item.Length * 2));");
            sb.AppendLine("        }");
            sb.AppendLine("        rpcWriter.GetSpan(checked(__encodedSize));");
            sb.AppendLine("        rpcWriter.Advance(0);");
        }
        sb.AppendLine($"        RpcGeneratedCodecWire.WriteCollectionCount(writer, {countExpression}, false);");

        if (model.Kind == GeneratedCodecKind.Dictionary)
        {
            sb.AppendLine("        foreach (var pair in value)");
            sb.AppendLine("        {");
            AppendLengthWrappedWrite(sb, "__keyCodec", "pair.Key", "key", 12);
            AppendLengthWrappedWrite(sb, "__valueCodec", "pair.Value", "value", 12);
            sb.AppendLine("        }");
            return;
        }

        sb.AppendLine($"        for (var index = 0; index < {countExpression}; index++)");
        sb.AppendLine("        {");
        sb.AppendLine($"            var item = {itemExpression};");
        AppendLengthWrappedWrite(sb, "__elementCodec", "item", "item", 12);
        sb.AppendLine("        }");
    }

    private static void AppendLengthWrappedWrite(
        StringBuilder sb,
        string codec,
        string value,
        string suffix,
        int spaces)
    {
        var indent = new string(' ', spaces);
        sb.AppendLine($"{indent}var {suffix}LengthToken = RpcGeneratedCodecWire.BeginLength(rpcWriter);");
        sb.AppendLine($"{indent}{codec}.Serialize({value}, writer);");
        sb.AppendLine($"{indent}RpcGeneratedCodecWire.EndLength(rpcWriter, {suffix}LengthToken);");
    }

    private static void AppendCollectionRead(StringBuilder sb, GeneratedCodecModel model)
    {
        sb.AppendLine("        var reader = new SequenceReader<byte>(buffer);");
        if (model.Kind == GeneratedCodecKind.Nullable)
        {
            sb.AppendLine("        if (!RpcGeneratedCodecWire.ReadPresence(ref reader))");
            sb.AppendLine("        {");
            sb.AppendLine("            RpcGeneratedCodecWire.EnsureFullyConsumed(reader);");
            sb.AppendLine("            return default;");
            sb.AppendLine("        }");
            sb.AppendLine("        var item = __elementCodec.Deserialize(reader.Sequence.Slice(reader.Position));");
            sb.AppendLine("        return item;");
            return;
        }

        sb.AppendLine("        var count = RpcGeneratedCodecWire.ReadCollectionCount(ref reader);");
        sb.AppendLine("        if (count < 0)");
        sb.AppendLine("        {");
        sb.AppendLine("            RpcGeneratedCodecWire.EnsureFullyConsumed(reader);");
        if (model.Kind is GeneratedCodecKind.Array or GeneratedCodecKind.List or GeneratedCodecKind.Dictionary)
            sb.AppendLine("            return null;");
        else
            sb.AppendLine("            return default;");
        sb.AppendLine("        }");

        if (model.Kind == GeneratedCodecKind.Dictionary)
        {
            sb.AppendLine($"        var result = new {model.TypeName}(count);");
            sb.AppendLine("        for (var index = 0; index < count; index++)");
            sb.AppendLine("        {");
            sb.AppendLine("            var key = __keyCodec.Deserialize(RpcGeneratedCodecWire.ReadLengthDelimited(ref reader));");
            sb.AppendLine("            var value = __valueCodec.Deserialize(RpcGeneratedCodecWire.ReadLengthDelimited(ref reader));");
            sb.AppendLine("            if (key is null)");
            sb.AppendLine("                throw RpcGeneratedCodecWire.DataLoss(\"Generated dictionary contains a null key.\");");
            sb.AppendLine("            if (!result.TryAdd(key!, value!))");
            sb.AppendLine("                throw RpcGeneratedCodecWire.DataLoss(\"Generated dictionary contains a duplicate key.\");");
            sb.AppendLine("        }");
            sb.AppendLine("        RpcGeneratedCodecWire.EnsureFullyConsumed(reader);");
            sb.AppendLine("        return result;");
            return;
        }

        if (model.Kind == GeneratedCodecKind.List)
        {
            sb.AppendLine($"        var result = new {model.TypeName}(count);");
            sb.AppendLine("        for (var index = 0; index < count; index++)");
            sb.AppendLine("            result.Add(__elementCodec.Deserialize(RpcGeneratedCodecWire.ReadLengthDelimited(ref reader))!);");
            sb.AppendLine("        RpcGeneratedCodecWire.EnsureFullyConsumed(reader);");
            sb.AppendLine("        return result;");
            return;
        }

        sb.AppendLine($"        var items = new {GetArrayCreationType(model.ElementType!, "count")};");
        sb.AppendLine("        for (var index = 0; index < count; index++)");
        sb.AppendLine("            items[index] = __elementCodec.Deserialize(RpcGeneratedCodecWire.ReadLengthDelimited(ref reader))!;");
        sb.AppendLine("        RpcGeneratedCodecWire.EnsureFullyConsumed(reader);");
        var returnExpression = model.Kind switch
        {
            GeneratedCodecKind.Array => "items",
            GeneratedCodecKind.Memory => $"new {model.TypeName}(items)",
            GeneratedCodecKind.ReadOnlyMemory => $"new {model.TypeName}(items)",
            GeneratedCodecKind.ImmutableArray => "ImmutableArray.CreateRange(items)",
            _ => "items"
        };
        sb.AppendLine($"        return {returnExpression};");
    }

    private static string GetArrayCreationType(string elementType, string lengthExpression)
    {
        if (elementType.EndsWith("[]", StringComparison.Ordinal))
        {
            var firstRank = elementType.IndexOf("[]", StringComparison.Ordinal);
            return elementType.Substring(0, firstRank) + "[" + lengthExpression + "]" +
                   elementType.Substring(firstRank);
        }
        return elementType + "[" + lengthExpression + "]";
    }
}
