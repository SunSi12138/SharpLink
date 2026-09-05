namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static void AppendUnionCodec(StringBuilder sb, GeneratedCodecModel model)
    {
        var cases = model.UnionCases
            .OrderBy(static item => item.Discriminator)
            .ThenBy(static item => item.TypeName, StringComparer.Ordinal)
            .ToImmutableArray();
        if (cases.IsDefaultOrEmpty)
            throw new InvalidOperationException($"Native union Codec '{model.TypeName}' has no cases.");

        sb.AppendLine($"internal sealed class {model.CodecName} : IRpcCodec<{model.TypeName}>");
        sb.AppendLine("{");
        for (var index = 0; index < cases.Length; index++)
            sb.AppendLine($"    private readonly IRpcCodec<{cases[index].TypeName}> __caseCodec{index};");
        sb.AppendLine();
        sb.AppendLine($"    public {model.CodecName}(IRpcCodecProvider provider)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(provider);");
        for (var index = 0; index < cases.Length; index++)
            sb.AppendLine($"        __caseCodec{index} = provider.GetCodec<{cases[index].TypeName}>();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine($"    public void Serialize(in {model.TypeName} value, IBufferWriter<byte> buffer)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(buffer);");
        sb.AppendLine("        if (value is null)");
        sb.AppendLine("        {");
        sb.AppendLine("            WriteDiscriminator(buffer, 0U);");
        sb.AppendLine("            return;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        switch (value)");
        sb.AppendLine("        {");
        for (var index = 0; index < cases.Length; index++)
        {
            var unionCase = cases[index];
            sb.AppendLine($"            case {unionCase.TypeName} caseValue{index}:");
            sb.AppendLine($"                WriteDiscriminator(buffer, {unionCase.Discriminator.ToString(InvariantCulture)}U);");
            sb.AppendLine($"                __caseCodec{index}.Serialize(in caseValue{index}, buffer);");
            sb.AppendLine("                return;");
        }
        sb.AppendLine("            default:");
        sb.AppendLine($"                throw new InvalidOperationException(\"Runtime value is not a declared case of native RPC union '{EscapeString(model.TypeName)}'.\");");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine($"    public {model.TypeName}? Deserialize(in ReadOnlySequence<byte> buffer)");
        sb.AppendLine("    {");
        sb.AppendLine("        var reader = new SequenceReader<byte>(buffer);");
        sb.AppendLine("        var discriminator = ReadDiscriminator(ref reader);");
        sb.AppendLine("        if (discriminator == 0U)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (!reader.End)");
        sb.AppendLine("                throw RpcGeneratedCodecWire.DataLoss(\"Native union null discriminator contains trailing payload bytes.\");");
        sb.AppendLine("            return null;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        var payload = reader.Sequence.Slice(reader.Position);");
        sb.AppendLine("        switch (discriminator)");
        sb.AppendLine("        {");
        for (var index = 0; index < cases.Length; index++)
        {
            var unionCase = cases[index];
            sb.AppendLine($"            case {unionCase.Discriminator.ToString(InvariantCulture)}U:");
            sb.AppendLine("            {");
            sb.AppendLine($"                var decoded = __caseCodec{index}.Deserialize(in payload);");
            if (unionCase.IsReferenceType)
            {
                sb.AppendLine("                if (decoded is null)");
                sb.AppendLine($"                    throw RpcGeneratedCodecWire.DataLoss(\"Native union discriminator {unionCase.Discriminator.ToString(InvariantCulture)} decoded a null case payload; null must use discriminator 0.\");");
            }
            sb.AppendLine("                return decoded;");
            sb.AppendLine("            }");
        }
        sb.AppendLine("            default:");
        sb.AppendLine($"                throw RpcGeneratedCodecWire.DataLoss($\"Native union '{EscapeString(model.TypeName)}' contains unknown discriminator {{discriminator}}.\");");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static void WriteDiscriminator(IBufferWriter<byte> writer, uint value)");
        sb.AppendLine("    {");
        sb.AppendLine("        while (value >= 0x80U)");
        sb.AppendLine("        {");
        sb.AppendLine("            var span = writer.GetSpan(1);");
        sb.AppendLine("            span[0] = (byte)((value & 0x7FU) | 0x80U);");
        sb.AppendLine("            writer.Advance(1);");
        sb.AppendLine("            value >>= 7;");
        sb.AppendLine("        }");
        sb.AppendLine("        var finalSpan = writer.GetSpan(1);");
        sb.AppendLine("        finalSpan[0] = (byte)value;");
        sb.AppendLine("        writer.Advance(1);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static uint ReadDiscriminator(ref SequenceReader<byte> reader)");
        sb.AppendLine("    {");
        sb.AppendLine("        uint value = 0;");
        sb.AppendLine("        for (var shift = 0; shift <= 28; shift += 7)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (!reader.TryRead(out var current))");
        sb.AppendLine("                throw RpcGeneratedCodecWire.DataLoss(\"Native union discriminator is truncated.\");");
        sb.AppendLine("            if (shift == 28 && (current & 0xF0) != 0)");
        sb.AppendLine("                throw RpcGeneratedCodecWire.DataLoss(\"Native union discriminator exceeds UInt32.\");");
        sb.AppendLine("            value |= (uint)(current & 0x7F) << shift;");
        sb.AppendLine("            if ((current & 0x80) == 0)");
        sb.AppendLine("            {");
        sb.AppendLine("                if (shift != 0 && current == 0)");
        sb.AppendLine("                    throw RpcGeneratedCodecWire.DataLoss(\"Native union discriminator uses a non-canonical varuint encoding.\");");
        sb.AppendLine("                return value;");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        throw RpcGeneratedCodecWire.DataLoss(\"Native union discriminator exceeds UInt32.\");");
        sb.AppendLine("    }");
        AppendFactory(sb, model);
        sb.AppendLine("}");
        sb.AppendLine();
    }
}