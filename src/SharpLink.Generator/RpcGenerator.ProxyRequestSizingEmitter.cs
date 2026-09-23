namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static void AppendGeneratedRequestSnapshotType(
        StringBuilder sb,
        RpcParameterModel[] parameters,
        RpcParameterModel[] complex)
    {
        sb.AppendLine("    private sealed class __SizedSnapshot : IRpcSizedCodecSnapshot");
        sb.AppendLine("    {");
        foreach (var parameter in parameters)
            sb.AppendLine($"        internal {parameter.DisplayType} __value_{parameter.Name} = default!;");
        foreach (var parameter in complex)
        {
            sb.AppendLine($"        internal int __nestedSize_{parameter.Name};");
            sb.AppendLine($"        internal IRpcSizedCodecSnapshot? __nestedSnapshot_{parameter.Name};");
        }
        sb.AppendLine();
        sb.AppendLine("        internal void Reset()");
        sb.AppendLine("        {");
        foreach (var parameter in parameters)
            sb.AppendLine($"            __value_{parameter.Name} = default!;");
        foreach (var parameter in complex)
        {
            sb.AppendLine($"            __nestedSize_{parameter.Name} = 0;");
            sb.AppendLine($"            __nestedSnapshot_{parameter.Name} = null;");
        }
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private __SizedSnapshot RentSnapshot()");
        sb.AppendLine("        => __sizedSnapshots.TryTake(out var snapshot) ? snapshot : new __SizedSnapshot();");
        sb.AppendLine();
        sb.AppendLine("    private void ReturnSnapshot(__SizedSnapshot snapshot)");
        sb.AppendLine("    {");
        sb.AppendLine("        snapshot.Reset();");
        sb.AppendLine("        __sizedSnapshots.Add(snapshot);");
        sb.AppendLine("    }");
    }

    private static void AppendGeneratedRequestSizingMethods(
        StringBuilder sb,
        string requestType,
        RpcParameterModel[] parameters,
        RpcParameterModel[] blittable,
        RpcParameterModel[] complex,
        bool useHotChildCores,
        IReadOnlyDictionary<string, string> concreteCodecTypes)
    {
        var fixedSize = blittable.Length == 0
            ? "0"
            : string.Join(" + ", blittable.Select(parameter => GetInlineSizeToken(parameter.Type)));

        sb.AppendLine(complex.Length == 0
            ? "    public bool CanExactSize => true;"
            : "    public bool CanExactSize => __canExactSize;");
        sb.AppendLine();
        sb.AppendLine($"    public bool TryGetEncodedSize(in {requestType} value, out int size)");
        sb.AppendLine("    {");
        sb.AppendLine($"        size = {fixedSize};");
        foreach (var parameter in complex)
        {
            var field = $"value.{EscapeIdentifier(parameter.Name)}";
            if (useHotChildCores &&
                TryGetStaticGeneratedCodecCoreType(parameter.Type, concreteCodecTypes, out _))
            {
                sb.AppendLine($"        if (!__codec_{parameter.Name}.CanExactSize ||");
                sb.AppendLine($"            !__codec_{parameter.Name}.TryGetEncodedSize({field}!, out var __nestedSize_{parameter.Name}))");
            }
            else
            {
                sb.AppendLine($"        var __sized_{parameter.Name} = __sizedCodec_{parameter.Name};");
                sb.AppendLine($"        if (__sized_{parameter.Name} is null ||");
                sb.AppendLine($"            !__sized_{parameter.Name}.CanExactSize ||");
                sb.AppendLine($"            !__sized_{parameter.Name}.TryGetEncodedSize({field}!, out var __nestedSize_{parameter.Name}))");
            }
            sb.AppendLine("        {");
            sb.AppendLine("            size = 0;");
            sb.AppendLine("            return false;");
            sb.AppendLine("        }");
            sb.AppendLine($"        size = checked(size + sizeof(int) + __nestedSize_{parameter.Name});");
        }
        sb.AppendLine("        return true;");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine($"    public bool TryGetEncodedSize(in {requestType} value, out int size, out IRpcSizedCodecSnapshot? snapshot)");
        sb.AppendLine("    {");
        if (complex.Length == 0)
        {
            sb.AppendLine($"        size = {fixedSize};");
            sb.AppendLine("        snapshot = null;");
            sb.AppendLine("        return true;");
        }
        else
        {
            sb.AppendLine($"        size = {fixedSize};");
            sb.AppendLine("        snapshot = null;");
            sb.AppendLine("        var __snapshot = RentSnapshot();");
            sb.AppendLine("        var __keepSnapshot = false;");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            foreach (var parameter in parameters)
                sb.AppendLine($"            __snapshot.__value_{parameter.Name} = value.{EscapeIdentifier(parameter.Name)};");
            foreach (var parameter in complex)
            {
                if (useHotChildCores &&
                    TryGetStaticGeneratedCodecCoreType(parameter.Type, concreteCodecTypes, out _))
                {
                    sb.AppendLine($"            if (!__codec_{parameter.Name}.CanExactSize ||");
                    sb.AppendLine($"                !__codec_{parameter.Name}.TryGetEncodedSize(__snapshot.__value_{parameter.Name}!, out __snapshot.__nestedSize_{parameter.Name}, out __snapshot.__nestedSnapshot_{parameter.Name}))");
                }
                else
                {
                    sb.AppendLine($"            var __sized_{parameter.Name} = __sizedCodec_{parameter.Name};");
                    sb.AppendLine($"            if (__sized_{parameter.Name} is null ||");
                    sb.AppendLine($"                !__sized_{parameter.Name}.CanExactSize ||");
                    sb.AppendLine($"                !__sized_{parameter.Name}.TryGetEncodedSize(__snapshot.__value_{parameter.Name}!, out __snapshot.__nestedSize_{parameter.Name}, out __snapshot.__nestedSnapshot_{parameter.Name}))");
                }
                sb.AppendLine("            {");
                sb.AppendLine("                size = 0;");
                sb.AppendLine("                return false;");
                sb.AppendLine("            }");
                sb.AppendLine($"            size = checked(size + sizeof(int) + __snapshot.__nestedSize_{parameter.Name});");
            }
            sb.AppendLine("            snapshot = __snapshot;");
            sb.AppendLine("            __keepSnapshot = true;");
            sb.AppendLine("            return true;");
            sb.AppendLine("        }");
            sb.AppendLine("        finally");
            sb.AppendLine("        {");
            sb.AppendLine("            if (!__keepSnapshot)");
            sb.AppendLine("            {");
            sb.AppendLine("                try");
            sb.AppendLine("                {");
            sb.AppendLine("                    ReleaseCapturedChildren(__snapshot);");
            sb.AppendLine("                }");
            sb.AppendLine("                finally");
            sb.AppendLine("                {");
            sb.AppendLine("                    ReturnSnapshot(__snapshot);");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
        }
        sb.AppendLine("    }");
        sb.AppendLine();

        if (complex.Length != 0)
        {
            sb.AppendLine("    private void ReleaseCapturedChildren(__SizedSnapshot snapshot)");
            sb.AppendLine("    {");
            foreach (var parameter in complex)
            {
                if (useHotChildCores &&
                    TryGetStaticGeneratedCodecCoreType(parameter.Type, concreteCodecTypes, out _))
                {
                    sb.AppendLine($"        if (snapshot.__nestedSnapshot_{parameter.Name} is not null)");
                    sb.AppendLine($"            __codec_{parameter.Name}.ReleaseSnapshot(snapshot.__nestedSnapshot_{parameter.Name});");
                }
                else
                {
                    sb.AppendLine($"        var __sized_{parameter.Name} = __sizedCodec_{parameter.Name};");
                    sb.AppendLine($"        if (__sized_{parameter.Name} is not null && snapshot.__nestedSnapshot_{parameter.Name} is not null)");
                    sb.AppendLine($"            __sized_{parameter.Name}.ReleaseSnapshot(snapshot.__nestedSnapshot_{parameter.Name});");
                }
            }
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.AppendLine($"    public void SerializeSized(in {requestType} value, IBufferWriter<byte> buffer, int size, IRpcSizedCodecSnapshot? snapshot)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(buffer);");
        if (complex.Length != 0)
        {
            sb.AppendLine("        if (snapshot is not __SizedSnapshot __snapshot)");
            sb.AppendLine("            throw new ArgumentException(\"Generated request sized serialization requires its captured snapshot.\", nameof(snapshot));");
        }
        if (blittable.Length != 0)
        {
            sb.AppendLine($"        var fixedSpan = buffer.GetSpan({fixedSize});");
            sb.AppendLine("        var fixedOffset = 0;");
            foreach (var parameter in blittable)
            {
                var value = complex.Length == 0
                    ? $"value.{EscapeIdentifier(parameter.Name)}"
                    : $"__snapshot.__value_{parameter.Name}";
                var parameterSize = GetInlineSizeToken(parameter.Type);
                if (IsBooleanType(parameter.Type))
                    sb.AppendLine($"        fixedSpan[fixedOffset] = {value} ? (byte)1 : (byte)0;");
                else
                    sb.AppendLine($"        Unsafe.WriteUnaligned(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(fixedSpan.Slice(fixedOffset, {parameterSize})), {value});");
                sb.AppendLine($"        fixedOffset += {parameterSize};");
            }
            sb.AppendLine($"        buffer.Advance({fixedSize});");
        }
        foreach (var parameter in complex)
        {
            sb.AppendLine($"        var lengthSpan_{parameter.Name} = buffer.GetSpan(sizeof(int));");
            sb.AppendLine($"        BinaryPrimitives.WriteInt32LittleEndian(lengthSpan_{parameter.Name}, __snapshot.__nestedSize_{parameter.Name});");
            sb.AppendLine("        buffer.Advance(sizeof(int));");
            if (useHotChildCores &&
                TryGetStaticGeneratedCodecCoreType(parameter.Type, concreteCodecTypes, out _))
            {
                sb.AppendLine($"        __codec_{parameter.Name}.SerializeSized(__snapshot.__value_{parameter.Name}!, buffer, __snapshot.__nestedSize_{parameter.Name}, __snapshot.__nestedSnapshot_{parameter.Name});");
            }
            else
            {
                sb.AppendLine($"        (__sizedCodec_{parameter.Name} ?? throw new InvalidOperationException(\"Generated request exact-size capability changed after binding.\")).SerializeSized(__snapshot.__value_{parameter.Name}!, buffer, __snapshot.__nestedSize_{parameter.Name}, __snapshot.__nestedSnapshot_{parameter.Name});");
            }
        }
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine("    public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)");
        sb.AppendLine("    {");
        if (complex.Length == 0)
        {
            sb.AppendLine("    }");
        }
        else
        {
            sb.AppendLine("        if (snapshot is not __SizedSnapshot __snapshot)");
            sb.AppendLine("            return;");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine("            ReleaseCapturedChildren(__snapshot);");
            sb.AppendLine("        }");
            sb.AppendLine("        finally");
            sb.AppendLine("        {");
            sb.AppendLine("            ReturnSnapshot(__snapshot);");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
        }
    }


}
