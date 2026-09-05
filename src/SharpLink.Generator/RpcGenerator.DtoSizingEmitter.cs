namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static void AppendDtoExactSerializeBody(
        StringBuilder sb,
        DtoCodecAnalysisModel model,
        Dictionary<string, int> complexIndexes)
    {
        sb.AppendLine("        if (__canExactSize)");
        sb.AppendLine("        {");

        for (var memberIndex = 0; memberIndex < model.Members.Length; memberIndex++)
        {
            var member = model.Members[memberIndex];
            var value = $"value.{EscapeIdentifier(member.Identifier)}";
            switch (member.Kind)
            {
                case GeneratedMemberKind.String:
                    sb.AppendLine($"            var __string_{memberIndex} = {value};");
                    sb.AppendLine(
                        $"            var __stringByteCount_{memberIndex} = __string_{memberIndex} is null ? 0 : __SharpLinkGeneratedUtf16.GetByteCount(__string_{memberIndex});");
                    break;
                case GeneratedMemberKind.Fixed:
                    sb.AppendLine($"            var __fixed_{memberIndex} = {value};");
                    break;
                case GeneratedMemberKind.NullableFixed:
                    sb.AppendLine($"            var __nullable_{memberIndex} = {value};");
                    break;
                case GeneratedMemberKind.Complex:
                    sb.AppendLine($"            var __complex_{memberIndex} = {value};");
                    break;
            }
        }

        var baseSize = model.IsReferenceType ? 2 : 1;
        foreach (var member in model.Members)
        {
            if (member.Kind != GeneratedMemberKind.Fixed)
                continue;
            baseSize = checked(baseSize + GetFieldKeySize(member.FieldId, GetFixedWireTypeValue(member.FixedSize)) + member.FixedSize);
        }

        sb.AppendLine($"            var __exactSize = {baseSize.ToString(InvariantCulture)};");
        sb.AppendLine("            var __canComputeExact = true;");
        for (var memberIndex = 0; memberIndex < model.Members.Length; memberIndex++)
        {
            var member = model.Members[memberIndex];
            if (member.Kind != GeneratedMemberKind.Complex)
                continue;

            var complexIndex = complexIndexes[member.Name];
            sb.AppendLine($"            var __nestedSize_{complexIndex} = 0;");
            sb.AppendLine(
                $"            if (__codec_{complexIndex} is IRpcSizedCodec<{member.TypeName}> __sized_{complexIndex} && __sized_{complexIndex}.CanExactSize");
            sb.AppendLine("            {");
            sb.AppendLine(
                $"                if (!__sized_{complexIndex}.TryGetEncodedSize(__complex_{memberIndex}, out __nestedSize_{complexIndex}))");
            sb.AppendLine("                    __canComputeExact = false;");
            sb.AppendLine("            }");
            sb.AppendLine("            else");
            sb.AppendLine("                __canComputeExact = false;");
        }

        sb.AppendLine("            if (__canComputeExact)");
        sb.AppendLine("            {");
        sb.AppendLine("                checked");
        sb.AppendLine("                {");
        for (var memberIndex = 0; memberIndex < model.Members.Length; memberIndex++)
        {
            var member = model.Members[memberIndex];
            switch (member.Kind)
            {
                case GeneratedMemberKind.String:
                    {
                        var nullSize = GetFieldKeySize(member.FieldId, 0);
                        var valueOverhead = GetFieldKeySize(member.FieldId, 6) + sizeof(uint);
                        sb.AppendLine(
                            $"                    __exactSize += __string_{memberIndex} is null ? {nullSize.ToString(InvariantCulture)} : {valueOverhead.ToString(InvariantCulture)} + __stringByteCount_{memberIndex};");
                        break;
                    }
                case GeneratedMemberKind.NullableFixed:
                    {
                        var nullSize = GetFieldKeySize(member.FieldId, 0);
                        var valueSize = GetFieldKeySize(member.FieldId, GetFixedWireTypeValue(member.FixedSize)) + member.FixedSize;
                        sb.AppendLine(
                            $"                    __exactSize += __nullable_{memberIndex}.HasValue ? {valueSize.ToString(InvariantCulture)} : {nullSize.ToString(InvariantCulture)};");
                        break;
                    }
                case GeneratedMemberKind.Complex:
                    {
                        var complexIndex = complexIndexes[member.Name];
                        var keySize = GetFieldKeySize(member.FieldId, 6);
                        sb.AppendLine(
                            $"                    __exactSize += {keySize.ToString(InvariantCulture)} + sizeof(uint) + __nestedSize_{complexIndex};");
                        break;
                    }
            }
        }
        sb.AppendLine("                }");
        sb.AppendLine("            }");

        sb.AppendLine("            if (__canComputeExact && writer is IRpcByteBufferWriter __exactWriter)");
        sb.AppendLine("            {");
        sb.AppendLine("                __exactWriter.GetSpan(checked(__exactSize + 4));");
        sb.AppendLine("                __exactWriter.Advance(0);");
        sb.AppendLine("                RpcGeneratedCodecSizing.Enter();");
        sb.AppendLine("                try");
        sb.AppendLine("                {");
        AppendDtoSerializeBody(
            sb,
            model,
            complexIndexes,
            useCachedStrings: true,
            useCachedMembers: true,
            indent: "                    ");
        sb.AppendLine("                }");
        sb.AppendLine("                finally");
        sb.AppendLine("                {");
        sb.AppendLine("                    RpcGeneratedCodecSizing.Exit();");
        sb.AppendLine("                }");
        sb.AppendLine("                return;");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
    }

    private static void AppendDtoEncodedSizeMethod(
        StringBuilder sb,
        DtoCodecAnalysisModel model,
        Dictionary<string, int> complexIndexes)
    {
        AppendDtoSnapshotType(sb, model, complexIndexes);

        sb.AppendLine("    private void ReleaseCapturedChildren(__SizedSnapshot snapshot)");
        sb.AppendLine("    {");
        for (var memberIndex = 0; memberIndex < model.Members.Length; memberIndex++)
        {
            var member = model.Members[memberIndex];
            if (member.Kind != GeneratedMemberKind.Complex)
                continue;
            var index = complexIndexes[member.Name];
            sb.AppendLine(
                $"        if (__codec_{index} is IRpcSizedCodec<{member.TypeName}> __sized_{index} && snapshot.__nestedSnapshot_{index} is not null)");
            sb.AppendLine($"            __sized_{index}.ReleaseSnapshot(snapshot.__nestedSnapshot_{index});");
        }
        sb.AppendLine("    }");
        sb.AppendLine();

        AppendDtoSizeOnlyEncodedSizeMethod(sb, model, complexIndexes);
        sb.AppendLine();
        sb.AppendLine($"    public bool TryGetEncodedSize(in {model.TypeName} value, out int size, out IRpcSizedCodecSnapshot? snapshot)");
        sb.AppendLine("    {");
        if (model.IsReferenceType)
        {
            sb.AppendLine("        if (value is null)");
            sb.AppendLine("        {");
            sb.AppendLine("            size = 1;");
            sb.AppendLine("            snapshot = null;");
            sb.AppendLine("            return true;");
            sb.AppendLine("        }");
        }

        sb.AppendLine("        var __snapshot = RentSnapshot();");
        var baseSize = model.IsReferenceType ? 2 : 1;
        foreach (var member in model.Members)
        {
            if (member.Kind != GeneratedMemberKind.Fixed)
                continue;
            baseSize = checked(baseSize + GetFieldKeySize(member.FieldId, GetFixedWireTypeValue(member.FixedSize)) + member.FixedSize);
        }

        sb.AppendLine($"        size = {baseSize.ToString(InvariantCulture)};");
        for (var memberIndex = 0; memberIndex < model.Members.Length; memberIndex++)
        {
            var member = model.Members[memberIndex];
            var value = $"value.{EscapeIdentifier(member.Identifier)}";
            switch (member.Kind)
            {
                case GeneratedMemberKind.String:
                    sb.AppendLine($"        __snapshot.__string_{memberIndex} = {value};");
                    sb.AppendLine(
                        $"        __snapshot.__stringByteCount_{memberIndex} = __snapshot.__string_{memberIndex} is null ? 0 : __SharpLinkGeneratedUtf16.GetByteCount(__snapshot.__string_{memberIndex});");
                    break;
                case GeneratedMemberKind.Fixed:
                    sb.AppendLine($"        __snapshot.__fixed_{memberIndex} = {value};");
                    break;
                case GeneratedMemberKind.NullableFixed:
                    sb.AppendLine($"        __snapshot.__nullable_{memberIndex} = {value};");
                    break;
                case GeneratedMemberKind.Complex:
                    {
                        sb.AppendLine($"        __snapshot.__complex_{memberIndex} = {value};");
                        var index = complexIndexes[member.Name];
                        sb.AppendLine(
                            $"        if (__codec_{index} is not IRpcSizedCodec<{member.TypeName}> __sized_{index} ||");
                        sb.AppendLine($"            !__sized_{index}.CanExactSize ||");
                        sb.AppendLine(
                            $"            !__sized_{index}.TryGetEncodedSize(__snapshot.__complex_{memberIndex}, out __snapshot.__nestedSize_{index}, out __snapshot.__nestedSnapshot_{index}))");
                        sb.AppendLine("        {");
                        sb.AppendLine("            size = 0;");
                        sb.AppendLine("            snapshot = null;");
                        sb.AppendLine("            ReleaseCapturedChildren(__snapshot);");
                        sb.AppendLine("            ReturnSnapshot(__snapshot);");
                        sb.AppendLine("            return false;");
                        sb.AppendLine("        }");
                        break;
                    }
            }
        }

        sb.AppendLine("        checked");
        sb.AppendLine("        {");
        for (var memberIndex = 0; memberIndex < model.Members.Length; memberIndex++)
        {
            var member = model.Members[memberIndex];
            switch (member.Kind)
            {
                case GeneratedMemberKind.String:
                    {
                        var nullSize = GetFieldKeySize(member.FieldId, 0);
                        var valueOverhead = GetFieldKeySize(member.FieldId, 6) + sizeof(uint);
                        sb.AppendLine(
                            $"            size += __snapshot.__string_{memberIndex} is null ? {nullSize.ToString(InvariantCulture)} : {valueOverhead.ToString(InvariantCulture)} + __snapshot.__stringByteCount_{memberIndex};");
                        break;
                    }
                case GeneratedMemberKind.NullableFixed:
                    {
                        var nullSize = GetFieldKeySize(member.FieldId, 0);
                        var valueSize = GetFieldKeySize(member.FieldId, GetFixedWireTypeValue(member.FixedSize)) + member.FixedSize;
                        sb.AppendLine(
                            $"            size += __snapshot.__nullable_{memberIndex}.HasValue ? {valueSize.ToString(InvariantCulture)} : {nullSize.ToString(InvariantCulture)};");
                        break;
                    }
                case GeneratedMemberKind.Complex:
                    {
                        var index = complexIndexes[member.Name];
                        var keySize = GetFieldKeySize(member.FieldId, 6);
                        sb.AppendLine(
                            $"            size += {keySize.ToString(InvariantCulture)} + sizeof(uint) + __snapshot.__nestedSize_{index};");
                        break;
                    }
            }
        }
        sb.AppendLine("        }");
        sb.AppendLine("        snapshot = __snapshot;");
        sb.AppendLine("        return true;");
        sb.AppendLine("    }");
        sb.AppendLine();

        AppendDtoSizedSerializeMethod(sb, model, complexIndexes);
    }

    private static void AppendDtoSizeOnlyEncodedSizeMethod(
        StringBuilder sb,
        DtoCodecAnalysisModel model,
        Dictionary<string, int> complexIndexes)
    {
        sb.AppendLine($"    public bool TryGetEncodedSize(in {model.TypeName} value, out int size)");
        sb.AppendLine("    {");
        if (model.IsReferenceType)
        {
            sb.AppendLine("        if (value is null)");
            sb.AppendLine("        {");
            sb.AppendLine("            size = 1;");
            sb.AppendLine("            return true;");
            sb.AppendLine("        }");
        }

        var baseSize = model.IsReferenceType ? 2 : 1;
        sb.AppendLine($"        size = {baseSize.ToString(InvariantCulture)};");

        for (var memberIndex = 0; memberIndex < model.Members.Length; memberIndex++)
        {
            var member = model.Members[memberIndex];
            var value = $"value.{EscapeIdentifier(member.Identifier)}";
            switch (member.Kind)
            {
                case GeneratedMemberKind.Fixed:
                    {
                        var keySize = GetFieldKeySize(member.FieldId, GetFixedWireTypeValue(member.FixedSize));
                        sb.AppendLine($"        size = checked(size + {keySize.ToString(InvariantCulture)} + {member.FixedSize.ToString(InvariantCulture)});");
                        break;
                    }
                case GeneratedMemberKind.NullableFixed:
                    {
                        var nullSize = GetFieldKeySize(member.FieldId, 0);
                        var valueSize = GetFieldKeySize(member.FieldId, GetFixedWireTypeValue(member.FixedSize)) + member.FixedSize;
                        sb.AppendLine($"        size = checked(size + ({value} is null ? {nullSize.ToString(InvariantCulture)} : {valueSize.ToString(InvariantCulture)}));");
                        break;
                    }
                case GeneratedMemberKind.String:
                    {
                        var nullSize = GetFieldKeySize(member.FieldId, 0);
                        var valueOverhead = GetFieldKeySize(member.FieldId, 6) + sizeof(uint);
                        sb.AppendLine($"        size = checked(size + ({value} is null ? {nullSize.ToString(InvariantCulture)} : {valueOverhead.ToString(InvariantCulture)} + __SharpLinkGeneratedUtf16.GetByteCount({value})));");
                        break;
                    }
                case GeneratedMemberKind.Complex:
                    {
                        var index = complexIndexes[member.Name];
                        var keySize = GetFieldKeySize(member.FieldId, 6);
                        sb.AppendLine($"        if (__codec_{index} is not IRpcSizedCodec<{member.TypeName}> __sized_{index} ||");
                        sb.AppendLine($"            !__sized_{index}.CanExactSize ||");
                        sb.AppendLine($"            !__sized_{index}.TryGetEncodedSize({value}, out var __nestedSize_{index}))");
                        sb.AppendLine("        {");
                        sb.AppendLine("            size = 0;");
                        sb.AppendLine("            return false;");
                        sb.AppendLine("        }");
                        sb.AppendLine($"        size = checked(size + {keySize.ToString(InvariantCulture)} + sizeof(uint) + __nestedSize_{index});");
                        break;
                    }
            }
        }

        sb.AppendLine("        return true;");
        sb.AppendLine("    }");
    }

    private static void AppendDtoSnapshotType(
        StringBuilder sb,
        DtoCodecAnalysisModel model,
        Dictionary<string, int> complexIndexes)
    {
        sb.AppendLine("    private sealed class __SizedSnapshot : IRpcSizedCodecSnapshot");
        sb.AppendLine("    {");
        for (var memberIndex = 0; memberIndex < model.Members.Length; memberIndex++)
        {
            var member = model.Members[memberIndex];
            switch (member.Kind)
            {
                case GeneratedMemberKind.String:
                    sb.AppendLine($"        public string? __string_{memberIndex};");
                    sb.AppendLine($"        public int __stringByteCount_{memberIndex};");
                    break;
                case GeneratedMemberKind.Fixed:
                    sb.AppendLine($"        public {member.TypeName} __fixed_{memberIndex};");
                    break;
                case GeneratedMemberKind.NullableFixed:
                    sb.AppendLine($"        public {member.TypeName} __nullable_{memberIndex};");
                    break;
                case GeneratedMemberKind.Complex:
                    sb.AppendLine($"        public {member.TypeName} __complex_{memberIndex} = default!;");
                    sb.AppendLine($"        public int __nestedSize_{complexIndexes[member.Name]};");
                    sb.AppendLine($"        public IRpcSizedCodecSnapshot? __nestedSnapshot_{complexIndexes[member.Name]};");
                    break;
            }
        }
        sb.AppendLine();
        sb.AppendLine("        public void Clear()");
        sb.AppendLine("        {");
        for (var memberIndex = 0; memberIndex < model.Members.Length; memberIndex++)
        {
            var member = model.Members[memberIndex];
            switch (member.Kind)
            {
                case GeneratedMemberKind.String:
                    sb.AppendLine($"            __string_{memberIndex} = null;");
                    sb.AppendLine($"            __stringByteCount_{memberIndex} = 0;");
                    break;
                case GeneratedMemberKind.Fixed:
                    sb.AppendLine($"            __fixed_{memberIndex} = default;");
                    break;
                case GeneratedMemberKind.NullableFixed:
                    sb.AppendLine($"            __nullable_{memberIndex} = default;");
                    break;
                case GeneratedMemberKind.Complex:
                    sb.AppendLine($"            __complex_{memberIndex} = default!;");
                    sb.AppendLine($"            __nestedSize_{complexIndexes[member.Name]} = 0;");
                    sb.AppendLine($"            __nestedSnapshot_{complexIndexes[member.Name]} = null;");
                    break;
            }
        }
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static void AppendDtoSizedSerializeMethod(
        StringBuilder sb,
        DtoCodecAnalysisModel model,
        Dictionary<string, int> complexIndexes)
    {
        var hasComplex = complexIndexes.Count != 0;
        sb.AppendLine($"    public void SerializeSized(in {model.TypeName} value, IBufferWriter<byte> buffer, int size, IRpcSizedCodecSnapshot? snapshot)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(buffer);");
        if (model.IsReferenceType)
        {
            sb.AppendLine("        if (value is null)");
            sb.AppendLine("        {");
            sb.AppendLine("            RpcGeneratedCodecWire.WritePresence(buffer, false);");
            sb.AppendLine("            return;");
            sb.AppendLine("        }");
        }
        sb.AppendLine("        if (snapshot is not __SizedSnapshot __snapshot)");
        sb.AppendLine("            throw new ArgumentException(\"Snapshot does not belong to this codec.\", nameof(snapshot));");
        if (hasComplex)
        {
            sb.AppendLine("        var rpcWriter = buffer as IRpcByteBufferWriter ?? throw new InvalidOperationException(\"Generated DTO Codecs require the SharpLink packet writer.\");");
        }
        if (model.IsReferenceType)
        {
            sb.AppendLine("        RpcGeneratedCodecWire.WritePresence(buffer, true);");
        }

        for (var memberIndex = 0; memberIndex < model.Members.Length; memberIndex++)
        {
            var member = model.Members[memberIndex];
            var fieldId = member.FieldId.ToString(InvariantCulture) + "U";
            switch (member.Kind)
            {
                case GeneratedMemberKind.Fixed:
                    sb.AppendLine($"        RpcGeneratedCodecWire.WriteFieldKey(buffer, {fieldId}, {GetWireType(member.FixedSize)});");
                    sb.AppendLine(GetFixedWriteExpression(member.TypeName, $"__snapshot.__fixed_{memberIndex}", 8, "buffer"));
                    break;
                case GeneratedMemberKind.NullableFixed:
                    sb.AppendLine($"        if (!__snapshot.__nullable_{memberIndex}.HasValue)");
                    sb.AppendLine($"            RpcGeneratedCodecWire.WriteFieldKey(buffer, {fieldId}, RpcGeneratedWireType.Null);");
                    sb.AppendLine("        else");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            RpcGeneratedCodecWire.WriteFieldKey(buffer, {fieldId}, {GetWireType(member.FixedSize)});");
                    sb.AppendLine(GetFixedWriteExpression(member.FixedTypeName!, $"__snapshot.__nullable_{memberIndex}.Value", 12, "buffer"));
                    sb.AppendLine("        }");
                    break;
                case GeneratedMemberKind.String:
                    sb.AppendLine($"        if (__snapshot.__string_{memberIndex} is null)");
                    sb.AppendLine($"            RpcGeneratedCodecWire.WriteFieldKey(buffer, {fieldId}, RpcGeneratedWireType.Null);");
                    sb.AppendLine("        else");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            RpcGeneratedCodecWire.WriteFieldKey(buffer, {fieldId}, RpcGeneratedWireType.LengthDelimited);");
                    sb.AppendLine(
                        $"            __SharpLinkGeneratedUtf16.WriteStringKnownSize(buffer, __snapshot.__string_{memberIndex}, __snapshot.__stringByteCount_{memberIndex});");
                    sb.AppendLine("        }");
                    break;
                case GeneratedMemberKind.Complex:
                    var index = complexIndexes[member.Name];
                    sb.AppendLine($"        RpcGeneratedCodecWire.WriteFieldKey(buffer, {fieldId}, RpcGeneratedWireType.LengthDelimited);");
                    sb.AppendLine($"        var lengthToken_{index} = RpcGeneratedCodecWire.BeginLength(rpcWriter);");
                    sb.AppendLine(
                        $"        if (__codec_{index} is IRpcSizedCodec<{member.TypeName}> __sized_{index})");
                    sb.AppendLine($"            __sized_{index}.SerializeSized(__snapshot.__complex_{memberIndex}, buffer, __snapshot.__nestedSize_{index}, __snapshot.__nestedSnapshot_{index});");
                    sb.AppendLine("        else");
                    sb.AppendLine($"            __codec_{index}.Serialize(__snapshot.__complex_{memberIndex}, buffer);");
                    sb.AppendLine($"        RpcGeneratedCodecWire.EndLength(rpcWriter, lengthToken_{index});");
                    break;
            }
        }

        sb.AppendLine("        RpcGeneratedCodecWire.WriteObjectEnd(buffer);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (snapshot is not __SizedSnapshot __snapshot)");
        sb.AppendLine("            return;");
        sb.AppendLine("        ReleaseCapturedChildren(__snapshot);");
        sb.AppendLine("        ReturnSnapshot(__snapshot);");
        sb.AppendLine("    }");
    }

    private static int GetFieldKeySize(uint fieldId, int wireType)
        => GetVarUInt32Size((fieldId << 3) | checked((uint)wireType));

    private static int GetFixedWireTypeValue(int fixedSize) => fixedSize switch
    {
        1 => 1,
        2 => 2,
        4 => 3,
        8 => 4,
        16 => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(fixedSize))
    };

    private static int GetVarUInt32Size(uint value)
        => value < 1U << 7 ? 1 :
            value < 1U << 14 ? 2 :
            value < 1U << 21 ? 3 :
            value < 1U << 28 ? 4 : 5;
}
