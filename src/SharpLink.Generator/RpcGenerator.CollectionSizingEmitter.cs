namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static bool IsDirectCollectionSizedType(string typeName)
        => IsCollectionStringType(typeName) || TryGetConstantSize(typeName, out _);

    private static bool IsCollectionStringType(string typeName)
        => typeName.Replace("global::", string.Empty) is "string" or "System.String";

    private static string GetCollectionSizedFieldName(string codecField)
        => codecField switch
        {
            "__elementCodec" => "__sizedElementCodec",
            "__keyCodec" => "__sizedKeyCodec",
            "__valueCodec" => "__sizedValueCodec",
            _ => throw new InvalidOperationException("Unknown collection Codec field.")
        };

    private static void AppendCollectionCoreCodecField(
        StringBuilder sb,
        string typeName,
        string codecField,
        IReadOnlyDictionary<string, string> concreteCodecTypes)
    {
        sb.AppendLine($"        private readonly {GetCodecHotStorageType(typeName, typeName, concreteCodecTypes)} {codecField};");
        if (!IsDirectCollectionSizedType(typeName) &&
            !TryGetStaticGeneratedCodecCoreType(typeName, concreteCodecTypes, out _))
        {
            sb.AppendLine($"        private readonly IRpcSizedCodec<{typeName}>? {GetCollectionSizedFieldName(codecField)};");
        }
    }

    private static void AppendCollectionCoreCodecBinding(
        StringBuilder sb,
        string typeName,
        string codecField,
        IReadOnlyDictionary<string, string> concreteCodecTypes)
    {
        if (TryGetStaticGeneratedCodecCoreType(typeName, concreteCodecTypes, out _))
        {
            sb.AppendLine($"            {codecField} = owner.{codecField}.StaticCore;");
            return;
        }

        sb.AppendLine($"            {codecField} = owner.{codecField};");
        if (!IsDirectCollectionSizedType(typeName))
        {
            sb.AppendLine(
                $"            {GetCollectionSizedFieldName(codecField)} = owner.{codecField} as IRpcSizedCodec<{typeName}>;");
        }
    }

    private static string GetCollectionCapabilityExpression(
        string typeName,
        string codecField,
        IReadOnlyDictionary<string, string> concreteCodecTypes)
    {
        if (IsDirectCollectionSizedType(typeName))
            return "true";
        if (TryGetStaticGeneratedCodecCoreType(typeName, concreteCodecTypes, out _))
            return $"{codecField}.CanExactSize";
        return $"{GetCollectionSizedFieldName(codecField)} is {{ CanExactSize: true }}";
    }

    private static void AppendCollectionSnapshotType(StringBuilder sb, GeneratedCodecModel model)
    {
        sb.AppendLine("    private sealed class __SizedSnapshot : IRpcSizedCodecSnapshot");
        sb.AppendLine("    {");
        if (model.Kind == GeneratedCodecKind.Nullable)
        {
            sb.AppendLine($"        internal {model.ElementType!} __item = default!;");
            sb.AppendLine("        internal int __nestedSize;");
            sb.AppendLine("        internal IRpcSizedCodecSnapshot? __nestedSnapshot;");
            sb.AppendLine("        internal void Reset()");
            sb.AppendLine("        {");
            sb.AppendLine("            __item = default!;");
            sb.AppendLine("            __nestedSize = 0;");
            sb.AppendLine("            __nestedSnapshot = null;");
            sb.AppendLine("        }");
        }
        else if (model.Kind == GeneratedCodecKind.Dictionary)
        {
            sb.AppendLine($"        internal {model.KeyType!}[] __keys = global::System.Array.Empty<{model.KeyType!}>();");
            sb.AppendLine($"        internal {model.ValueType!}[] __values = global::System.Array.Empty<{model.ValueType!}>();");
            sb.AppendLine("        internal int[] __keySizes = global::System.Array.Empty<int>();");
            sb.AppendLine("        internal int[] __valueSizes = global::System.Array.Empty<int>();");
            sb.AppendLine("        internal IRpcSizedCodecSnapshot?[] __keySnapshots = global::System.Array.Empty<IRpcSizedCodecSnapshot?>();");
            sb.AppendLine("        internal IRpcSizedCodecSnapshot?[] __valueSnapshots = global::System.Array.Empty<IRpcSizedCodecSnapshot?>();");
            sb.AppendLine("        internal int __count;");
            sb.AppendLine();
            sb.AppendLine("        internal void EnsureCapacity(int count)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (__keys.Length >= count) return;");
            sb.AppendLine("            var capacity = Math.Max(count, Math.Max(4, __keys.Length * 2));");
            sb.AppendLine("            global::System.Array.Resize(ref __keys, capacity);");
            sb.AppendLine("            global::System.Array.Resize(ref __values, capacity);");
            sb.AppendLine("            global::System.Array.Resize(ref __keySizes, capacity);");
            sb.AppendLine("            global::System.Array.Resize(ref __valueSizes, capacity);");
            sb.AppendLine("            global::System.Array.Resize(ref __keySnapshots, capacity);");
            sb.AppendLine("            global::System.Array.Resize(ref __valueSnapshots, capacity);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        internal void Reset()");
            sb.AppendLine("        {");
            sb.AppendLine("            if (__count != 0)");
            sb.AppendLine("            {");
            sb.AppendLine("                global::System.Array.Clear(__keys, 0, __count);");
            sb.AppendLine("                global::System.Array.Clear(__values, 0, __count);");
            sb.AppendLine("                global::System.Array.Clear(__keySnapshots, 0, __count);");
            sb.AppendLine("                global::System.Array.Clear(__valueSnapshots, 0, __count);");
            sb.AppendLine("            }");
            sb.AppendLine("            __count = 0;");
            sb.AppendLine("        }");
        }
        else
        {
            sb.AppendLine($"        internal {model.ElementType!}[] __items = global::System.Array.Empty<{model.ElementType!}>();");
            sb.AppendLine("        internal int[] __nestedSizes = global::System.Array.Empty<int>();");
            sb.AppendLine("        internal IRpcSizedCodecSnapshot?[] __nestedSnapshots = global::System.Array.Empty<IRpcSizedCodecSnapshot?>();");
            sb.AppendLine("        internal int __count;");
            sb.AppendLine();
            sb.AppendLine("        internal void EnsureCapacity(int count)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (__items.Length >= count) return;");
            sb.AppendLine("            var capacity = Math.Max(count, Math.Max(4, __items.Length * 2));");
            sb.AppendLine("            global::System.Array.Resize(ref __items, capacity);");
            sb.AppendLine("            global::System.Array.Resize(ref __nestedSizes, capacity);");
            sb.AppendLine("            global::System.Array.Resize(ref __nestedSnapshots, capacity);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        internal void Reset()");
            sb.AppendLine("        {");
            sb.AppendLine("            if (__count != 0)");
            sb.AppendLine("            {");
            sb.AppendLine("                global::System.Array.Clear(__items, 0, __count);");
            sb.AppendLine("                global::System.Array.Clear(__nestedSnapshots, 0, __count);");
            sb.AppendLine("            }");
            sb.AppendLine("            __count = 0;");
            sb.AppendLine("        }");
        }
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private __SizedSnapshot RentSnapshot()");
        sb.AppendLine("        => __sizedSnapshots.TryTake(out var snapshot) ? snapshot : new __SizedSnapshot();");
        sb.AppendLine("    private void ReturnSnapshot(__SizedSnapshot snapshot)");
        sb.AppendLine("    {");
        sb.AppendLine("        snapshot.Reset();");
        sb.AppendLine("        __sizedSnapshots.Add(snapshot);");
        sb.AppendLine("    }");
    }

    private static void AppendCollectionCoreSizing(
        StringBuilder sb,
        GeneratedCodecModel model,
        IReadOnlyDictionary<string, string> concreteCodecTypes)
    {
        sb.AppendLine("    public bool CanExactSize => __canExactSize;");
        sb.AppendLine();

        if (model.Kind != GeneratedCodecKind.Nullable)
        {
            sb.AppendLine("    private static int GetCountSize(int count, bool isNull)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (isNull) return 1;");
            sb.AppendLine("        if ((uint)count > RpcGeneratedCodecWire.MaximumCollectionItems)");
            sb.AppendLine("            throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted, $\"Generated collection contains more than {RpcGeneratedCodecWire.MaximumCollectionItems} items.\");");
            sb.AppendLine("        var marker = checked((uint)count + 1U);");
            sb.AppendLine("        var bytes = 1;");
            sb.AppendLine("        while (marker >= 0x80) { bytes++; marker >>= 7; }");
            sb.AppendLine("        return bytes;");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        AppendCollectionSizeOnlyMethod(sb, model, concreteCodecTypes);
        sb.AppendLine();
        AppendCollectionSnapshotSizeMethod(sb, model, concreteCodecTypes);
        sb.AppendLine();
        AppendCollectionReleaseCaptured(sb, model, concreteCodecTypes);
        sb.AppendLine();
        AppendCollectionSerializeSized(sb, model, concreteCodecTypes);
        sb.AppendLine();
        sb.AppendLine("    public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (snapshot is not __SizedSnapshot __snapshot) return;");
        sb.AppendLine("        try { ReleaseCapturedChildren(__snapshot); }");
        sb.AppendLine("        finally { ReturnSnapshot(__snapshot); }");
        sb.AppendLine("    }");
    }

    private static void AppendCollectionSizeOnlyMethod(
        StringBuilder sb,
        GeneratedCodecModel model,
        IReadOnlyDictionary<string, string> concreteCodecTypes)
    {
        sb.AppendLine($"    public bool TryGetEncodedSize(in {model.TypeName} value, out int size)");
        sb.AppendLine("    {");
        if (model.Kind == GeneratedCodecKind.Nullable)
        {
            sb.AppendLine("        if (!value.HasValue) { size = 1; return true; }");
            AppendCollectionSizeOnlyChild(
                sb, model.ElementType!, "__elementCodec", "value.Value", "__nestedSize", 8, concreteCodecTypes);
            sb.AppendLine("        size = checked(1 + __nestedSize);");
            sb.AppendLine("        return true;");
            sb.AppendLine("    }");
            return;
        }

        var nullCondition = GetCollectionNullCondition(model, "value");
        if (nullCondition is not null)
            sb.AppendLine($"        if ({nullCondition}) {{ size = 1; return true; }}");

        var count = GetCollectionCountExpression(model, "value");
        sb.AppendLine($"        var __count = {count};");
        sb.AppendLine("        size = GetCountSize(__count, false);");
        if (model.Kind == GeneratedCodecKind.Dictionary)
        {
            sb.AppendLine("        foreach (var __pair in value)");
            sb.AppendLine("        {");
            AppendCollectionSizeOnlyChild(
                sb, model.KeyType!, "__keyCodec", "__pair.Key", "__keySize", 12, concreteCodecTypes);
            AppendCollectionSizeOnlyChild(
                sb, model.ValueType!, "__valueCodec", "__pair.Value", "__valueSize", 12, concreteCodecTypes);
            sb.AppendLine("            size = checked(size + sizeof(uint) + __keySize + sizeof(uint) + __valueSize);");
            sb.AppendLine("        }");
        }
        else
        {
            sb.AppendLine("        for (var __index = 0; __index < __count; __index++)");
            sb.AppendLine("        {");
            var item = GetCollectionItemExpression(model, "value", "__index");
            AppendCollectionSizeOnlyChild(
                sb, model.ElementType!, "__elementCodec", item, "__nestedSize", 12, concreteCodecTypes);
            sb.AppendLine("            size = checked(size + sizeof(uint) + __nestedSize);");
            sb.AppendLine("        }");
        }
        sb.AppendLine("        return true;");
        sb.AppendLine("    }");
    }

    private static void AppendCollectionSnapshotSizeMethod(
        StringBuilder sb,
        GeneratedCodecModel model,
        IReadOnlyDictionary<string, string> concreteCodecTypes)
    {
        sb.AppendLine($"    public bool TryGetEncodedSize(in {model.TypeName} value, out int size, out IRpcSizedCodecSnapshot? snapshot)");
        sb.AppendLine("    {");
        if (model.Kind == GeneratedCodecKind.Nullable)
        {
            sb.AppendLine("        if (!value.HasValue) { size = 1; snapshot = null; return true; }");
            sb.AppendLine("        size = 0;");
            sb.AppendLine("        snapshot = null;");
            sb.AppendLine("        var __snapshot = RentSnapshot();");
            sb.AppendLine("        var __keepSnapshot = false;");
            sb.AppendLine("        __snapshot.__item = value.Value;");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            AppendCollectionSnapshotChild(
                sb, model.ElementType!, "__elementCodec", "__snapshot.__item",
                "__snapshot.__nestedSize", "__snapshot.__nestedSnapshot", 12, concreteCodecTypes);
            sb.AppendLine("            size = checked(1 + __snapshot.__nestedSize);");
            sb.AppendLine("            snapshot = __snapshot;");
            sb.AppendLine("            __keepSnapshot = true;");
            sb.AppendLine("            return true;");
            sb.AppendLine("        }");
            sb.AppendLine("        finally");
            sb.AppendLine("        {");
            sb.AppendLine("            if (!__keepSnapshot)");
            sb.AppendLine("            {");
            sb.AppendLine("                try { ReleaseCapturedChildren(__snapshot); }");
            sb.AppendLine("                finally { ReturnSnapshot(__snapshot); }");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            return;
        }

        var nullCondition = GetCollectionNullCondition(model, "value");
        if (nullCondition is not null)
            sb.AppendLine($"        if ({nullCondition}) {{ size = 1; snapshot = null; return true; }}");

        var count = GetCollectionCountExpression(model, "value");
        sb.AppendLine($"        var __count = {count};");
        sb.AppendLine("        size = GetCountSize(__count, false);");
        sb.AppendLine("        snapshot = null;");
        sb.AppendLine("        var __snapshot = RentSnapshot();");
        sb.AppendLine("        var __keepSnapshot = false;");
        sb.AppendLine("        __snapshot.EnsureCapacity(__count);");
        sb.AppendLine("        try");
        sb.AppendLine("        {");
        if (model.Kind == GeneratedCodecKind.Dictionary)
        {
            sb.AppendLine("            var __index = 0;");
            sb.AppendLine("            foreach (var __pair in value)");
            sb.AppendLine("            {");
            sb.AppendLine("                __snapshot.__keys[__index] = __pair.Key;");
            sb.AppendLine("                __snapshot.__values[__index] = __pair.Value;");
            sb.AppendLine("                __snapshot.__count = __index + 1;");
            AppendCollectionSnapshotChild(
                sb, model.KeyType!, "__keyCodec", "__snapshot.__keys[__index]",
                "__snapshot.__keySizes[__index]", "__snapshot.__keySnapshots[__index]", 16, concreteCodecTypes);
            AppendCollectionSnapshotChild(
                sb, model.ValueType!, "__valueCodec", "__snapshot.__values[__index]",
                "__snapshot.__valueSizes[__index]", "__snapshot.__valueSnapshots[__index]", 16, concreteCodecTypes);
            sb.AppendLine("                size = checked(size + sizeof(uint) + __snapshot.__keySizes[__index] + sizeof(uint) + __snapshot.__valueSizes[__index]);");
            sb.AppendLine("                __index++;");
            sb.AppendLine("            }");
            sb.AppendLine("            if (__index != __count)");
            sb.AppendLine("                throw new InvalidOperationException(\"Generated dictionary changed size while exact sizing was in progress.\");");
        }
        else
        {
            sb.AppendLine("            for (var __index = 0; __index < __count; __index++)");
            sb.AppendLine("            {");
            var item = GetCollectionItemExpression(model, "value", "__index");
            sb.AppendLine($"                __snapshot.__items[__index] = {item};");
            sb.AppendLine("                __snapshot.__count = __index + 1;");
            AppendCollectionSnapshotChild(
                sb, model.ElementType!, "__elementCodec", "__snapshot.__items[__index]",
                "__snapshot.__nestedSizes[__index]", "__snapshot.__nestedSnapshots[__index]", 16, concreteCodecTypes);
            sb.AppendLine("                size = checked(size + sizeof(uint) + __snapshot.__nestedSizes[__index]);");
            sb.AppendLine("            }");
        }
        sb.AppendLine("            snapshot = __snapshot;");
        sb.AppendLine("            __keepSnapshot = true;");
        sb.AppendLine("            return true;");
        sb.AppendLine("        }");
        sb.AppendLine("        finally");
        sb.AppendLine("        {");
        sb.AppendLine("            if (!__keepSnapshot)");
        sb.AppendLine("            {");
        sb.AppendLine("                try { ReleaseCapturedChildren(__snapshot); }");
        sb.AppendLine("                finally { ReturnSnapshot(__snapshot); }");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }

    private static void AppendCollectionReleaseCaptured(
        StringBuilder sb,
        GeneratedCodecModel model,
        IReadOnlyDictionary<string, string> concreteCodecTypes)
    {
        sb.AppendLine("    private void ReleaseCapturedChildren(__SizedSnapshot snapshot)");
        sb.AppendLine("    {");
        if (model.Kind == GeneratedCodecKind.Nullable)
        {
            AppendCollectionReleaseChild(
                sb, model.ElementType!, "__elementCodec", "snapshot.__nestedSnapshot", 8, concreteCodecTypes);
        }
        else if (model.Kind == GeneratedCodecKind.Dictionary)
        {
            sb.AppendLine("        for (var __index = 0; __index < snapshot.__count; __index++)");
            sb.AppendLine("        {");
            AppendCollectionReleaseChild(
                sb, model.KeyType!, "__keyCodec", "snapshot.__keySnapshots[__index]", 12, concreteCodecTypes);
            AppendCollectionReleaseChild(
                sb, model.ValueType!, "__valueCodec", "snapshot.__valueSnapshots[__index]", 12, concreteCodecTypes);
            sb.AppendLine("        }");
        }
        else
        {
            sb.AppendLine("        for (var __index = 0; __index < snapshot.__count; __index++)");
            sb.AppendLine("        {");
            AppendCollectionReleaseChild(
                sb, model.ElementType!, "__elementCodec", "snapshot.__nestedSnapshots[__index]", 12, concreteCodecTypes);
            sb.AppendLine("        }");
        }
        sb.AppendLine("    }");
    }

    private static void AppendCollectionSerializeSized(
        StringBuilder sb,
        GeneratedCodecModel model,
        IReadOnlyDictionary<string, string> concreteCodecTypes)
    {
        sb.AppendLine($"    public void SerializeSized(in {model.TypeName} value, IBufferWriter<byte> buffer, int size, IRpcSizedCodecSnapshot? snapshot)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(buffer);");
        if (model.Kind == GeneratedCodecKind.Nullable)
        {
            sb.AppendLine("        if (!value.HasValue) { RpcGeneratedCodecWire.WritePresence(buffer, false); return; }");
            sb.AppendLine("        if (snapshot is not __SizedSnapshot __snapshot)");
            sb.AppendLine("            throw new ArgumentException(\"Generated nullable collection Codec sized serialization requires its captured snapshot.\", nameof(snapshot));");
            sb.AppendLine("        RpcGeneratedCodecWire.WritePresence(buffer, true);");
            AppendCollectionSerializeSizedChild(
                sb, model.ElementType!, "__elementCodec", "__snapshot.__item",
                "__snapshot.__nestedSize", "__snapshot.__nestedSnapshot", 8, concreteCodecTypes);
            sb.AppendLine("    }");
            return;
        }

        var nullCondition = GetCollectionNullCondition(model, "value");
        if (nullCondition is not null)
            sb.AppendLine($"        if ({nullCondition}) {{ RpcGeneratedCodecWire.WriteCollectionCount(buffer, 0, true); return; }}");
        sb.AppendLine("        if (snapshot is not __SizedSnapshot __snapshot)");
        sb.AppendLine("            throw new ArgumentException(\"Generated collection sized serialization requires its captured snapshot.\", nameof(snapshot));");
        sb.AppendLine("        RpcGeneratedCodecWire.WriteCollectionCount(buffer, __snapshot.__count, false);");
        if (model.Kind == GeneratedCodecKind.Dictionary)
        {
            sb.AppendLine("        for (var __index = 0; __index < __snapshot.__count; __index++)");
            sb.AppendLine("        {");
            sb.AppendLine("            var __keyLength = buffer.GetSpan(sizeof(uint));");
            sb.AppendLine("            global::System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(__keyLength, checked((uint)__snapshot.__keySizes[__index]));");
            sb.AppendLine("            buffer.Advance(sizeof(uint));");
            AppendCollectionSerializeSizedChild(
                sb, model.KeyType!, "__keyCodec", "__snapshot.__keys[__index]",
                "__snapshot.__keySizes[__index]", "__snapshot.__keySnapshots[__index]", 12, concreteCodecTypes);
            sb.AppendLine("            var __valueLength = buffer.GetSpan(sizeof(uint));");
            sb.AppendLine("            global::System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(__valueLength, checked((uint)__snapshot.__valueSizes[__index]));");
            sb.AppendLine("            buffer.Advance(sizeof(uint));");
            AppendCollectionSerializeSizedChild(
                sb, model.ValueType!, "__valueCodec", "__snapshot.__values[__index]",
                "__snapshot.__valueSizes[__index]", "__snapshot.__valueSnapshots[__index]", 12, concreteCodecTypes);
            sb.AppendLine("        }");
        }
        else
        {
            sb.AppendLine("        for (var __index = 0; __index < __snapshot.__count; __index++)");
            sb.AppendLine("        {");
            sb.AppendLine("            var __length = buffer.GetSpan(sizeof(uint));");
            sb.AppendLine("            global::System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(__length, checked((uint)__snapshot.__nestedSizes[__index]));");
            sb.AppendLine("            buffer.Advance(sizeof(uint));");
            AppendCollectionSerializeSizedChild(
                sb, model.ElementType!, "__elementCodec", "__snapshot.__items[__index]",
                "__snapshot.__nestedSizes[__index]", "__snapshot.__nestedSnapshots[__index]", 12, concreteCodecTypes);
            sb.AppendLine("        }");
        }
        sb.AppendLine("    }");
    }

    private static void AppendCollectionSizeOnlyChild(
        StringBuilder sb,
        string typeName,
        string codecField,
        string valueExpression,
        string sizeVariable,
        int spaces,
        IReadOnlyDictionary<string, string> concreteCodecTypes)
    {
        var indent = new string(' ', spaces);
        if (IsCollectionStringType(typeName))
        {
            sb.AppendLine($"{indent}if ({valueExpression} is {{ }} __stringValue && __stringValue.Length > (RpcGeneratedCodecWire.MaximumStringPayloadBytes / sizeof(char)))");
            sb.AppendLine($"{indent}    throw new ArgumentOutOfRangeException(nameof({valueExpression}), \"Serialized payload exceeds the protocol maximum.\");");
            sb.AppendLine($"{indent}var {sizeVariable} = {valueExpression} is null ? sizeof(int) : checked(sizeof(int) + {valueExpression}.Length * sizeof(char));");
            return;
        }
        if (TryGetConstantSize(typeName, out _))
        {
            sb.AppendLine($"{indent}var {sizeVariable} = {GetInlineSizeToken(typeName)};");
            return;
        }
        if (TryGetStaticGeneratedCodecCoreType(typeName, concreteCodecTypes, out _))
        {
            sb.AppendLine($"{indent}if (!{codecField}.CanExactSize || !{codecField}.TryGetEncodedSize({valueExpression}!, out var {sizeVariable}))");
        }
        else
        {
            var sized = GetCollectionSizedFieldName(codecField);
            sb.AppendLine($"{indent}var __sized = {sized};");
            sb.AppendLine($"{indent}if (__sized is null || !__sized.CanExactSize || !__sized.TryGetEncodedSize({valueExpression}!, out var {sizeVariable}))");
        }
        sb.AppendLine($"{indent}{{ size = 0; return false; }}");
    }

    private static void AppendCollectionSnapshotChild(
        StringBuilder sb,
        string typeName,
        string codecField,
        string valueExpression,
        string sizeTarget,
        string snapshotTarget,
        int spaces,
        IReadOnlyDictionary<string, string> concreteCodecTypes)
    {
        var indent = new string(' ', spaces);
        if (IsCollectionStringType(typeName))
        {
            sb.AppendLine($"{indent}if ({valueExpression} is {{ }} __stringValue && __stringValue.Length > (RpcGeneratedCodecWire.MaximumStringPayloadBytes / sizeof(char)))");
            sb.AppendLine($"{indent}    throw new ArgumentOutOfRangeException(nameof({valueExpression}), \"Serialized payload exceeds the protocol maximum.\");");
            sb.AppendLine($"{indent}{sizeTarget} = {valueExpression} is null ? sizeof(int) : checked(sizeof(int) + {valueExpression}.Length * sizeof(char));");
            sb.AppendLine($"{indent}{snapshotTarget} = null;");
            return;
        }
        if (TryGetConstantSize(typeName, out _))
        {
            sb.AppendLine($"{indent}{sizeTarget} = {GetInlineSizeToken(typeName)};");
            sb.AppendLine($"{indent}{snapshotTarget} = null;");
            return;
        }
        if (TryGetStaticGeneratedCodecCoreType(typeName, concreteCodecTypes, out _))
        {
            sb.AppendLine($"{indent}if (!{codecField}.CanExactSize || !{codecField}.TryGetEncodedSize({valueExpression}!, out {sizeTarget}, out {snapshotTarget}))");
        }
        else
        {
            var sized = GetCollectionSizedFieldName(codecField);
            sb.AppendLine($"{indent}var __sized = {sized};");
            sb.AppendLine($"{indent}if (__sized is null || !__sized.CanExactSize || !__sized.TryGetEncodedSize({valueExpression}!, out {sizeTarget}, out {snapshotTarget}))");
        }
        sb.AppendLine($"{indent}{{ size = 0; return false; }}");
    }

    private static void AppendCollectionReleaseChild(
        StringBuilder sb,
        string typeName,
        string codecField,
        string snapshotExpression,
        int spaces,
        IReadOnlyDictionary<string, string> concreteCodecTypes)
    {
        if (IsDirectCollectionSizedType(typeName))
            return;
        var indent = new string(' ', spaces);
        if (TryGetStaticGeneratedCodecCoreType(typeName, concreteCodecTypes, out _))
        {
            sb.AppendLine($"{indent}if ({snapshotExpression} is not null) {codecField}.ReleaseSnapshot({snapshotExpression});");
        }
        else
        {
            sb.AppendLine($"{indent}if ({snapshotExpression} is not null) {GetCollectionSizedFieldName(codecField)}?.ReleaseSnapshot({snapshotExpression});");
        }
    }

    private static void AppendCollectionSerializeSizedChild(
        StringBuilder sb,
        string typeName,
        string codecField,
        string valueExpression,
        string sizeExpression,
        string snapshotExpression,
        int spaces,
        IReadOnlyDictionary<string, string> concreteCodecTypes)
    {
        var indent = new string(' ', spaces);
        if (IsDirectCollectionSizedType(typeName))
        {
            sb.AppendLine($"{indent}{codecField}.Serialize({valueExpression}!, buffer);");
            return;
        }
        if (TryGetStaticGeneratedCodecCoreType(typeName, concreteCodecTypes, out _))
        {
            sb.AppendLine($"{indent}{codecField}.SerializeSized({valueExpression}!, buffer, {sizeExpression}, {snapshotExpression});");
        }
        else
        {
            sb.AppendLine($"{indent}({GetCollectionSizedFieldName(codecField)} ?? throw new InvalidOperationException(\"Generated collection exact-size capability changed after binding.\")).SerializeSized({valueExpression}!, buffer, {sizeExpression}, {snapshotExpression});");
        }
    }

    private static string? GetCollectionNullCondition(GeneratedCodecModel model, string valueExpression)
        => model.Kind switch
        {
            GeneratedCodecKind.Array or GeneratedCodecKind.List or GeneratedCodecKind.Dictionary
                => $"{valueExpression} is null",
            GeneratedCodecKind.ImmutableArray => $"{valueExpression}.IsDefault",
            _ => null
        };

    private static string GetCollectionCountExpression(GeneratedCodecModel model, string valueExpression)
        => model.Kind is GeneratedCodecKind.List or GeneratedCodecKind.Dictionary
            ? $"{valueExpression}.Count"
            : $"{valueExpression}.Length";

    private static string GetCollectionItemExpression(
        GeneratedCodecModel model,
        string valueExpression,
        string indexExpression)
        => model.Kind is GeneratedCodecKind.Memory or GeneratedCodecKind.ReadOnlyMemory
            ? $"{valueExpression}.Span[{indexExpression}]"
            : $"{valueExpression}[{indexExpression}]";
}
