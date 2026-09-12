namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static void AppendDtoSerializeMethod(
        StringBuilder sb,
        DtoCodecAnalysisModel model,
        Dictionary<string, int> complexIndexes,
        bool hasDirectString,
        bool hasComplex)
    {
        sb.AppendLine($"    public void Serialize(in {model.TypeName} value, IBufferWriter<byte> writer)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(writer);");
        if (hasComplex)
        {
            sb.AppendLine("        var rpcWriter = writer as IRpcByteBufferWriter ?? throw new InvalidOperationException(\"Generated DTO Codecs require the SharpLink packet writer.\");");
        }
        if (model.IsReferenceType)
        {
            sb.AppendLine("        if (value is null)");
            sb.AppendLine("        {");
            sb.AppendLine("            RpcGeneratedCodecWire.WritePresence(writer, false);");
            sb.AppendLine("            return;");
            sb.AppendLine("        }");
        }

        if (hasComplex)
        {
            sb.AppendLine("        if (__canExactSize && writer is IRpcByteBufferWriter __exactWriter)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (TryGetEncodedSize(in value, out var __exactSize, out var __sizedSnapshot) && __sizedSnapshot is not null)");
            sb.AppendLine("            {");
            sb.AppendLine("                try");
            sb.AppendLine("                {");
            sb.AppendLine("                    __exactWriter.GetSpan(checked(__exactSize + 4));");
            sb.AppendLine("                    __exactWriter.Advance(0);");
            sb.AppendLine("                    SerializeSized(in value, writer, __exactSize, __sizedSnapshot);");
            sb.AppendLine("                }");
            sb.AppendLine("                finally");
            sb.AppendLine("                {");
            sb.AppendLine("                    ReleaseSnapshot(__sizedSnapshot);");
            sb.AppendLine("                }");
            sb.AppendLine("                return;");
            sb.AppendLine("            }");
            sb.AppendLine("        }");

            if (hasDirectString)
            {
                AppendDtoDirectPreReservation(sb, model);
                AppendDtoSerializeBody(sb, model, complexIndexes, useCachedStrings: true, useCachedMembers: true, indent: "        ");
            }
            else
            {
                AppendDtoSerializeBody(sb, model, complexIndexes, useCachedStrings: false, useCachedMembers: false, indent: "        ");
            }
        }
        else if (hasDirectString)
        {
            AppendDtoDirectPreReservation(sb, model);
            AppendDtoSerializeBody(sb, model, complexIndexes, useCachedStrings: true, useCachedMembers: true, indent: "        ");
        }
        else
        {
            AppendDtoSerializeBody(sb, model, complexIndexes, useCachedStrings: false, useCachedMembers: false, indent: "        ");
        }

        sb.AppendLine("    }");
    }

    private static void AppendDtoSerializeBody(
        StringBuilder sb,
        DtoCodecAnalysisModel model,
        Dictionary<string, int> complexIndexes,
        bool useCachedStrings,
        bool useCachedMembers,
        string indent)
    {
        if (model.IsReferenceType)
            sb.AppendLine($"{indent}RpcGeneratedCodecWire.WritePresence(writer, true);");

        for (var memberIndex = 0; memberIndex < model.Members.Length; memberIndex++)
        {
            AppendDtoMemberWrite(
                sb,
                model.Members[memberIndex],
                complexIndexes,
                useCachedStrings ? memberIndex : -1,
                useCachedMembers,
                indent);
        }

        sb.AppendLine($"{indent}RpcGeneratedCodecWire.WriteObjectEnd(writer);");
    }

    private static void AppendDtoSuppressedSerializeBody(
        StringBuilder sb,
        DtoCodecAnalysisModel model,
        Dictionary<string, int> complexIndexes,
        string indent)
    {
        for (var memberIndex = 0; memberIndex < model.Members.Length; memberIndex++)
        {
            var member = model.Members[memberIndex];
            if (member.Kind != GeneratedMemberKind.String)
                continue;

            var value = $"value.{EscapeIdentifier(member.Identifier)}";
            sb.AppendLine($"{indent}var __string_{memberIndex} = {value};");
            sb.AppendLine(
                $"{indent}var __stringByteCount_{memberIndex} = __string_{memberIndex} is null ? 0 : __SharpLinkGeneratedUtf16.GetByteCount(__string_{memberIndex});");
        }

        AppendDtoSerializeBody(sb, model, complexIndexes, useCachedStrings: true, useCachedMembers: false, indent: indent);
    }

    private static void AppendDtoMemberWrite(
        StringBuilder sb,
        DtoMemberAnalysisModel member,
        Dictionary<string, int> complexIndexes,
        int cachedMemberIndex,
        bool useCachedMembers,
        string indent)
    {
        var value = cachedMemberIndex < 0
            ? $"value.{EscapeIdentifier(member.Identifier)}"
            : member.Kind switch
            {
                GeneratedMemberKind.String => $"__string_{cachedMemberIndex}",
                GeneratedMemberKind.Fixed when useCachedMembers => $"__fixed_{cachedMemberIndex}",
                GeneratedMemberKind.NullableFixed when useCachedMembers => $"__nullable_{cachedMemberIndex}",
                GeneratedMemberKind.Complex when useCachedMembers => $"__complex_{cachedMemberIndex}",
                _ => $"value.{EscapeIdentifier(member.Identifier)}"
            };
        var fieldId = member.FieldId.ToString(InvariantCulture) + "U";
        var childIndent = indent + "    ";
        switch (member.Kind)
        {
            case GeneratedMemberKind.Fixed:
                sb.AppendLine($"{indent}RpcGeneratedCodecWire.WriteFieldKey(writer, {fieldId}, {GetWireType(member.FixedSize)});");
                sb.AppendLine(GetFixedWriteExpression(member.TypeName, value, indent.Length));
                break;
            case GeneratedMemberKind.NullableFixed:
                sb.AppendLine($"{indent}if (!{value}.HasValue)");
                sb.AppendLine($"{childIndent}RpcGeneratedCodecWire.WriteFieldKey(writer, {fieldId}, RpcGeneratedWireType.Null);");
                sb.AppendLine($"{indent}else");
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{childIndent}RpcGeneratedCodecWire.WriteFieldKey(writer, {fieldId}, {GetWireType(member.FixedSize)});");
                sb.AppendLine(GetFixedWriteExpression(member.FixedTypeName!, value + ".Value", childIndent.Length));
                sb.AppendLine($"{indent}}}");
                break;
            case GeneratedMemberKind.String:
                sb.AppendLine($"{indent}if ({value} is null)");
                sb.AppendLine($"{childIndent}RpcGeneratedCodecWire.WriteFieldKey(writer, {fieldId}, RpcGeneratedWireType.Null);");
                sb.AppendLine($"{indent}else");
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{childIndent}RpcGeneratedCodecWire.WriteFieldKey(writer, {fieldId}, RpcGeneratedWireType.LengthDelimited);");
                if (cachedMemberIndex >= 0)
                {
                    sb.AppendLine(
                        $"{childIndent}__SharpLinkGeneratedUtf16.WriteStringKnownSize(writer, {value}, __stringByteCount_{cachedMemberIndex});");
                }
                else
                {
                    sb.AppendLine($"{childIndent}RpcGeneratedCodecWire.WriteString(writer, {value});");
                }
                sb.AppendLine($"{indent}}}");
                break;
            default:
                var index = complexIndexes[member.Name];
                sb.AppendLine($"{indent}RpcGeneratedCodecWire.WriteFieldKey(writer, {fieldId}, RpcGeneratedWireType.LengthDelimited);");
                sb.AppendLine($"{indent}var lengthToken_{index} = RpcGeneratedCodecWire.BeginLength(rpcWriter);");
                sb.AppendLine($"{indent}__codec_{index}.Serialize({value}, writer);");
                sb.AppendLine($"{indent}RpcGeneratedCodecWire.EndLength(rpcWriter, lengthToken_{index});");
                break;
        }
    }

    private static void AppendDtoDirectPreReservation(StringBuilder sb, DtoCodecAnalysisModel model)
    {
        for (var memberIndex = 0; memberIndex < model.Members.Length; memberIndex++)
        {
            var member = model.Members[memberIndex];
            var value = $"value.{EscapeIdentifier(member.Identifier)}";
            if (member.Kind == GeneratedMemberKind.String)
            {
                sb.AppendLine($"        var __string_{memberIndex} = {value};");
                sb.AppendLine(
                    $"        var __stringByteCount_{memberIndex} = __string_{memberIndex} is null ? 0 : __SharpLinkGeneratedUtf16.GetByteCount(__string_{memberIndex});");
            }
            else if (member.Kind == GeneratedMemberKind.Fixed)
            {
                sb.AppendLine($"        var __fixed_{memberIndex} = {value};");
            }
            else if (member.Kind == GeneratedMemberKind.NullableFixed)
            {
                sb.AppendLine($"        var __nullable_{memberIndex} = {value};");
            }
            else if (member.Kind == GeneratedMemberKind.Complex)
            {
                sb.AppendLine($"        var __complex_{memberIndex} = {value};");
            }
        }

        var baseSize = model.IsReferenceType ? 2 : 1;
        foreach (var member in model.Members)
        {
            if (member.Kind != GeneratedMemberKind.Fixed)
                continue;
            baseSize = checked(baseSize + GetFieldKeySize(member.FieldId, GetFixedWireTypeValue(member.FixedSize)) + member.FixedSize);
        }

        sb.AppendLine($"        var __encodedSize = {baseSize.ToString(InvariantCulture)};");
        sb.AppendLine("        checked");
        sb.AppendLine("        {");
        for (var memberIndex = 0; memberIndex < model.Members.Length; memberIndex++)
        {
            var member = model.Members[memberIndex];
            if (member.Kind == GeneratedMemberKind.String)
            {
                var nullSize = GetFieldKeySize(member.FieldId, 0);
                var valueOverhead = GetFieldKeySize(member.FieldId, 6) + sizeof(uint);
                sb.AppendLine(
                    $"            __encodedSize += __string_{memberIndex} is null ? {nullSize.ToString(InvariantCulture)} : {valueOverhead.ToString(InvariantCulture)} + __stringByteCount_{memberIndex};");
            }
            else if (member.Kind == GeneratedMemberKind.NullableFixed)
            {
                var nullSize = GetFieldKeySize(member.FieldId, 0);
                var valueSize = GetFieldKeySize(member.FieldId, GetFixedWireTypeValue(member.FixedSize)) + member.FixedSize;
                sb.AppendLine(
                    $"            __encodedSize += __nullable_{memberIndex}.HasValue ? {valueSize.ToString(InvariantCulture)} : {nullSize.ToString(InvariantCulture)};");
            }
        }
        sb.AppendLine("        }");
        // Existing varuint primitives request five bytes even when they advance only one. Reserving
        // four bytes beyond the exact wire size prevents the terminator from forcing another growth
        // and preserves the bounded writer's established successful-capacity threshold. Restrict the
        // whole-payload reservation to the SharpLink packet writer, which supports a single large
        // contiguous lease; segmented or generic writers retain the per-field streaming path.
        sb.AppendLine("        if (writer is IRpcByteBufferWriter __rpcWriter)");
        sb.AppendLine("        {");
        sb.AppendLine("            __rpcWriter.GetSpan(checked(__encodedSize + 4));");
        sb.AppendLine("            __rpcWriter.Advance(0);");
        sb.AppendLine("        }");
    }

    private static string GetFixedWriteExpression(string typeName, string value, int spaces, string writerName = "writer")
    {
        var indent = new string(' ', spaces);
        if (IsBooleanType(typeName))
            return $"{indent}RpcGeneratedCodecWire.WriteBoolean({writerName}, {value});";
        var semanticMethod = GetSemanticFixedMethod(typeName);
        return semanticMethod is null
            ? $"{indent}RpcGeneratedCodecWire.WriteUnmanaged<{typeName}>({writerName}, {value});"
            : $"{indent}RpcGeneratedCodecWire.Write{semanticMethod}({writerName}, {value});";
    }
}
