namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static void AppendUnionCodec(StringBuilder sb, GeneratedCodecModel model)
    {
        var cases = model.Members
            .OrderBy(static member => member.FieldId)
            .ThenBy(static member => member.TypeName, StringComparer.Ordinal)
            .ToArray();
        sb.AppendLine($"internal sealed class {model.CodecName} : IRpcCodec<{model.TypeName}>");
        sb.AppendLine("{");
        for (var index = 0; index < cases.Length; index++)
            sb.AppendLine($"    private readonly IRpcCodec<{cases[index].TypeName}> __codec_{index};");
        sb.AppendLine();
        sb.AppendLine($"    internal {model.CodecName}(IRpcCodecProvider provider)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(provider);");
        for (var index = 0; index < cases.Length; index++)
            sb.AppendLine($"        __codec_{index} = provider.GetCodec<{cases[index].TypeName}>();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine($"    public void Serialize(in {model.TypeName} value, IBufferWriter<byte> writer)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(writer);");
        sb.AppendLine("        if (value is null)");
        sb.AppendLine("        {");
        sb.AppendLine("            __WriteDiscriminator(writer, 0);");
        sb.AppendLine("            return;");
        sb.AppendLine("        }");
        sb.AppendLine("        switch (value)");
        sb.AppendLine("        {");
        for (var index = 0; index < cases.Length; index++)
        {
            var discriminator = checked((int)cases[index].FieldId);
            sb.AppendLine($"            case {cases[index].TypeName} __case_{index}:");
            sb.AppendLine($"                __WriteDiscriminator(writer, {discriminator.ToString(InvariantCulture)});");
            sb.AppendLine($"                __codec_{index}.Serialize(__case_{index}, writer);");
            sb.AppendLine("                return;");
        }
        sb.AppendLine("            default:");
        sb.AppendLine($"                throw new SharpLinkException(SharpLinkErrorCode.InvalidArgument, \"Union '{EscapeString(model.TypeName)}' received a runtime value that is not one of its declared cases.\");");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine($"    public {model.TypeName}? Deserialize(in ReadOnlySequence<byte> buffer)");
        sb.AppendLine("    {");
        sb.AppendLine("        var reader = new SequenceReader<byte>(buffer);");
        sb.AppendLine("        var discriminator = __ReadDiscriminator(ref reader);");
        sb.AppendLine("        if (discriminator == 0)");
        sb.AppendLine("        {");
        sb.AppendLine("            RpcGeneratedCodecWire.EnsureFullyConsumed(reader);");
        sb.AppendLine("            return null;");
        sb.AppendLine("        }");
        sb.AppendLine("        switch (discriminator)");
        sb.AppendLine("        {");
        for (var index = 0; index < cases.Length; index++)
        {
            var discriminator = checked((int)cases[index].FieldId);
            sb.AppendLine($"            case {discriminator.ToString(InvariantCulture)}:");
            sb.AppendLine("            {");
            sb.AppendLine($"                var decoded = __codec_{index}.Deserialize(reader.Sequence.Slice(reader.Position));");
            if (cases[index].Nullable)
            {
                sb.AppendLine("                if (decoded is null)");
                sb.AppendLine($"                    throw RpcGeneratedCodecWire.DataLoss(\"Union '{EscapeString(model.TypeName)}' case {discriminator.ToString(InvariantCulture)} decoded a null concrete value.\");");
            }
            sb.AppendLine("                return decoded;");
            sb.AppendLine("            }");
        }
        sb.AppendLine("            default:");
        sb.AppendLine($"                throw RpcGeneratedCodecWire.DataLoss($\"Union '{EscapeString(model.TypeName)}' contains unknown discriminator {{discriminator}}.\");");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static void __WriteDiscriminator(IBufferWriter<byte> writer, int discriminator)");
        sb.AppendLine("    {");
        sb.AppendLine("        var span = writer.GetSpan(sizeof(int));");
        sb.AppendLine("        global::System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span, discriminator);");
        sb.AppendLine("        writer.Advance(sizeof(int));");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static int __ReadDiscriminator(ref SequenceReader<byte> reader)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (reader.Remaining < sizeof(int))");
        sb.AppendLine("            throw RpcGeneratedCodecWire.DataLoss(\"Union discriminator is truncated.\");");
        sb.AppendLine("        if (reader.UnreadSpan.Length >= sizeof(int))");
        sb.AppendLine("        {");
        sb.AppendLine("            var value = global::System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(reader.UnreadSpan);");
        sb.AppendLine("            reader.Advance(sizeof(int));");
        sb.AppendLine("            return value;");
        sb.AppendLine("        }");
        sb.AppendLine("        Span<byte> temporary = stackalloc byte[sizeof(int)];");
        sb.AppendLine("        if (!reader.TryCopyTo(temporary))");
        sb.AppendLine("            throw RpcGeneratedCodecWire.DataLoss(\"Union discriminator is truncated.\");");
        sb.AppendLine("        reader.Advance(sizeof(int));");
        sb.AppendLine("        return global::System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(temporary);");
        sb.AppendLine("    }");
        AppendFactory(sb, model);
        sb.AppendLine("}");
        sb.AppendLine();
    }
}
