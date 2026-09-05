namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static void AppendDtoDeserializeMethod(
        StringBuilder sb,
        DtoCodecAnalysisModel model,
        Dictionary<string, int> complexIndexes)
    {
        var returnType = model.IsReferenceType ? model.TypeName + "?" : model.TypeName;
        sb.AppendLine($"    public {returnType} Deserialize(in ReadOnlySequence<byte> buffer)");
        sb.AppendLine("    {");
        sb.AppendLine("        var reader = new SequenceReader<byte>(buffer);");
        if (model.IsReferenceType)
        {
            sb.AppendLine("        if (!RpcGeneratedCodecWire.ReadPresence(ref reader))");
            sb.AppendLine("        {");
            sb.AppendLine("            RpcGeneratedCodecWire.EnsureFullyConsumed(reader);");
            sb.AppendLine("            return null;");
            sb.AppendLine("        }");
        }
        foreach (var member in model.Members)
        {
            sb.AppendLine($"        {member.TypeName} local_{member.Identifier} = default!;");
            if (member.Required)
                sb.AppendLine($"        var seen_{member.Identifier} = false;");
        }
        sb.AppendLine("        while (RpcGeneratedCodecWire.TryReadField(ref reader, out var fieldId, out var wireType))");
        sb.AppendLine("        {");
        sb.AppendLine("            switch (fieldId)");
        sb.AppendLine("            {");
        foreach (var member in model.Members)
            AppendDtoMemberRead(sb, member, complexIndexes);
        sb.AppendLine("                default:");
        sb.AppendLine("                    RpcGeneratedCodecWire.SkipField(ref reader, wireType);");
        sb.AppendLine("                    break;");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        RpcGeneratedCodecWire.EnsureFullyConsumed(reader);");
        foreach (var member in model.Members.Where(static member => member.Required))
        {
            sb.AppendLine($"        if (!seen_{member.Identifier})");
            sb.AppendLine($"            throw RpcGeneratedCodecWire.DataLoss(\"Missing required RPC member '{EscapeString(member.Name)}'.\");");
            if (member.NonNullableReference)
            {
                sb.AppendLine($"        if (local_{member.Identifier} is null)");
                sb.AppendLine($"            throw RpcGeneratedCodecWire.DataLoss(\"Required RPC member '{EscapeString(member.Name)}' cannot be null.\");");
            }
        }

        var memberByName = model.Members.ToDictionary(static member => member.Name, StringComparer.Ordinal);
        sb.Append($"        return new {model.TypeName}(");
        for (var index = 0; index < model.ConstructorMembers.Length; index++)
        {
            if (index != 0)
                sb.Append(", ");
            sb.Append("local_").Append(memberByName[model.ConstructorMembers[index]].Identifier);
        }
        sb.Append(')');
        var initializerMembers = model.Members.Where(static member => member.InitializerBound).ToArray();
        if (initializerMembers.Length != 0)
        {
            sb.AppendLine();
            sb.AppendLine("        {");
            for (var index = 0; index < initializerMembers.Length; index++)
            {
                var member = initializerMembers[index];
                var suffix = index == initializerMembers.Length - 1 ? string.Empty : ",";
                sb.AppendLine($"            {EscapeIdentifier(member.Identifier)} = local_{member.Identifier}{suffix}");
            }
            sb.Append("        }");
        }
        sb.AppendLine(";");
        sb.AppendLine("    }");
    }

    private static void AppendDtoMemberRead(
        StringBuilder sb,
        DtoMemberAnalysisModel member,
        Dictionary<string, int> complexIndexes)
    {
        sb.AppendLine($"                case {member.FieldId.ToString(InvariantCulture)}U:");
        if (member.Required)
            sb.AppendLine($"                    seen_{member.Identifier} = true;");
        switch (member.Kind)
        {
            case GeneratedMemberKind.Fixed:
                sb.AppendLine($"                    RpcGeneratedCodecWire.EnsureWireType(wireType, {GetWireType(member.FixedSize)});");
                sb.AppendLine(GetFixedReadExpression(member.TypeName, $"local_{member.Identifier}", 20));
                break;
            case GeneratedMemberKind.NullableFixed:
                sb.AppendLine("                    if (wireType == RpcGeneratedWireType.Null)");
                sb.AppendLine($"                        local_{member.Identifier} = default;");
                sb.AppendLine("                    else");
                sb.AppendLine("                    {");
                sb.AppendLine($"                        RpcGeneratedCodecWire.EnsureWireType(wireType, {GetWireType(member.FixedSize)});");
                sb.AppendLine(GetFixedReadExpression(member.FixedTypeName!, $"local_{member.Identifier}", 24));
                sb.AppendLine("                    }");
                break;
            case GeneratedMemberKind.String:
                sb.AppendLine("                    if (wireType == RpcGeneratedWireType.Null)");
                sb.AppendLine($"                        local_{member.Identifier} = null!;");
                sb.AppendLine("                    else");
                sb.AppendLine("                    {");
                sb.AppendLine("                        RpcGeneratedCodecWire.EnsureWireType(wireType, RpcGeneratedWireType.LengthDelimited);");
                sb.AppendLine($"                        local_{member.Identifier} = RpcGeneratedCodecWire.ReadString(ref reader);");
                sb.AppendLine("                    }");
                break;
            default:
                var index = complexIndexes[member.Name];
                sb.AppendLine("                    RpcGeneratedCodecWire.EnsureWireType(wireType, RpcGeneratedWireType.LengthDelimited);");
                sb.AppendLine($"                    local_{member.Identifier} = __codec_{index}.Deserialize(RpcGeneratedCodecWire.ReadLengthDelimited(ref reader))!;");
                break;
        }
        sb.AppendLine("                    break;");
    }

    private static string GetFixedReadExpression(string typeName, string target, int spaces)
    {
        var indent = new string(' ', spaces);
        if (IsBooleanType(typeName))
            return $"{indent}{target} = RpcGeneratedCodecWire.ReadBoolean(ref reader);";
        var semanticMethod = GetSemanticFixedMethod(typeName);
        return semanticMethod is null
            ? $"{indent}{target} = RpcGeneratedCodecWire.ReadUnmanaged<{typeName}>(ref reader);"
            : $"{indent}{target} = RpcGeneratedCodecWire.Read{semanticMethod}(ref reader);";
    }
}
