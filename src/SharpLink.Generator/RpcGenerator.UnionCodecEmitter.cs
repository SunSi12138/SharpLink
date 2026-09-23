namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static void AppendUnionCodec(
        StringBuilder sb,
        GeneratedCodecModel model,
        IReadOnlyDictionary<string, string> concreteCodecTypes)
    {
        var cases = model.Members
            .OrderBy(static member => member.FieldId)
            .ThenBy(static member => member.TypeName, StringComparer.Ordinal)
            .ToArray();
        sb.AppendLine($"internal sealed class {model.CodecName} : IRpcCodec<{model.TypeName}>, IRpcSizedCodec<{model.TypeName}>");
        sb.AppendLine("{");
        for (var index = 0; index < cases.Length; index++)
            sb.AppendLine($"    private readonly {GetCodecStorageType(cases[index].TypeName, cases[index].TypeName, concreteCodecTypes)} __codec_{index};");
        sb.AppendLine("    private readonly global::System.Collections.Concurrent.ConcurrentBag<__SizedSnapshot> __sizedSnapshots = new();");
        sb.AppendLine();
        sb.AppendLine($"    internal {model.CodecName}(IRpcCodecProvider provider)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(provider);");
        for (var index = 0; index < cases.Length; index++)
            sb.AppendLine($"        __codec_{index} = {GetCodecResolveExpression("provider", cases[index].TypeName, cases[index].TypeName, concreteCodecTypes)};");
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
        sb.AppendLine();
        sb.AppendLine("    public bool CanExactSize => StaticCore.CanExactSize;");
        sb.AppendLine($"    public bool TryGetEncodedSize(in {model.TypeName} value, out int size)");
        sb.AppendLine("        => StaticCore.TryGetEncodedSize(in value, out size);");
        sb.AppendLine($"    public bool TryGetEncodedSize(in {model.TypeName} value, out int size, out IRpcSizedCodecSnapshot? snapshot)");
        sb.AppendLine("        => StaticCore.TryGetEncodedSize(in value, out size, out snapshot);");
        sb.AppendLine($"    public void SerializeSized(in {model.TypeName} value, IBufferWriter<byte> buffer, int size, IRpcSizedCodecSnapshot? snapshot)");
        sb.AppendLine("        => StaticCore.SerializeSized(in value, buffer, size, snapshot);");
        sb.AppendLine("    public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)");
        sb.AppendLine("        => StaticCore.ReleaseSnapshot(snapshot);");
        AppendFactory(sb, model);
        AppendUnionStaticCore(sb, model, cases, concreteCodecTypes);
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void AppendUnionStaticCore(
        StringBuilder sb,
        GeneratedCodecModel model,
        GeneratedMemberModel[] cases,
        IReadOnlyDictionary<string, string> concreteCodecTypes)
    {
        sb.AppendLine();
        sb.AppendLine("    private sealed class __SizedSnapshot : IRpcSizedCodecSnapshot");
        sb.AppendLine("    {");
        sb.AppendLine($"        internal {model.TypeName}? __value;");
        sb.AppendLine("        internal int __discriminator;");
        sb.AppendLine("        internal int __nestedSize;");
        sb.AppendLine("        internal IRpcSizedCodecSnapshot? __nestedSnapshot;");
        sb.AppendLine("        internal void Reset()");
        sb.AppendLine("        {");
        sb.AppendLine("            __value = null;");
        sb.AppendLine("            __discriminator = 0;");
        sb.AppendLine("            __nestedSize = 0;");
        sb.AppendLine("            __nestedSnapshot = null;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private __SizedSnapshot RentSnapshot()");
        sb.AppendLine("        => __sizedSnapshots.TryTake(out var snapshot) ? snapshot : new __SizedSnapshot();");
        sb.AppendLine("    private void ReturnSnapshot(__SizedSnapshot snapshot)");
        sb.AppendLine("    {");
        sb.AppendLine("        snapshot.Reset();");
        sb.AppendLine("        __sizedSnapshots.Add(snapshot);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    internal Core StaticCore => new(this);");
        sb.AppendLine();
        sb.AppendLine($"    internal readonly struct Core : IRpcCodec<{model.TypeName}>, IRpcSizedCodec<{model.TypeName}>");
        sb.AppendLine("    {");
        sb.AppendLine($"        private readonly {model.CodecName} __owner;");
        for (var index = 0; index < cases.Length; index++)
        {
            var caseType = cases[index].TypeName;
            sb.AppendLine($"        private readonly {GetCodecHotStorageType(caseType, caseType, concreteCodecTypes)} __codec_{index};");
            if (!TryGetStaticGeneratedCodecCoreType(caseType, concreteCodecTypes, out _))
                sb.AppendLine($"        private readonly IRpcSizedCodec<{caseType}>? __sizedCodec_{index};");
        }
        sb.AppendLine("        private readonly bool __canExactSize;");
        sb.AppendLine();
        sb.AppendLine($"        internal Core({model.CodecName} owner)");
        sb.AppendLine("        {");
        sb.AppendLine("            __owner = owner;");
        for (var index = 0; index < cases.Length; index++)
        {
            var caseType = cases[index].TypeName;
            if (TryGetStaticGeneratedCodecCoreType(caseType, concreteCodecTypes, out _))
            {
                sb.AppendLine($"            __codec_{index} = owner.__codec_{index}.StaticCore;");
            }
            else
            {
                sb.AppendLine($"            __codec_{index} = owner.__codec_{index};");
                sb.AppendLine($"            __sizedCodec_{index} = (object)owner.__codec_{index} as IRpcSizedCodec<{caseType}>;");
            }
        }
        if (cases.Length == 0)
        {
            sb.AppendLine("            __canExactSize = true;");
        }
        else
        {
            var checks = cases.Select((member, index) =>
                TryGetStaticGeneratedCodecCoreType(member.TypeName, concreteCodecTypes, out _)
                    ? $"__codec_{index}.CanExactSize"
                    : $"__sizedCodec_{index} is {{ CanExactSize: true }}");
            sb.AppendLine($"            __canExactSize = {string.Join(" && ", checks)};");
        }
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        private __SizedSnapshot RentSnapshot() => __owner.RentSnapshot();");
        sb.AppendLine("        private void ReturnSnapshot(__SizedSnapshot snapshot) => __owner.ReturnSnapshot(snapshot);");
        sb.AppendLine();
        sb.AppendLine("        public bool CanExactSize => __canExactSize;");
        sb.AppendLine();

        sb.AppendLine($"        public void Serialize(in {model.TypeName} value, IBufferWriter<byte> writer)");
        sb.AppendLine("        {");
        sb.AppendLine("            ArgumentNullException.ThrowIfNull(writer);");
        sb.AppendLine("            if (value is null)");
        sb.AppendLine("            {");
        sb.AppendLine("                __WriteDiscriminator(writer, 0);");
        sb.AppendLine("                return;");
        sb.AppendLine("            }");
        sb.AppendLine("            switch (value)");
        sb.AppendLine("            {");
        for (var index = 0; index < cases.Length; index++)
        {
            var discriminator = checked((int)cases[index].FieldId);
            sb.AppendLine($"                case {cases[index].TypeName} __case_{index}:");
            sb.AppendLine($"                    __WriteDiscriminator(writer, {discriminator.ToString(InvariantCulture)});");
            sb.AppendLine($"                    __codec_{index}.Serialize(__case_{index}, writer);");
            sb.AppendLine("                    return;");
        }
        sb.AppendLine("                default:");
        sb.AppendLine($"                    throw new SharpLinkException(SharpLinkErrorCode.InvalidArgument, \"Union '{EscapeString(model.TypeName)}' received a runtime value that is not one of its declared cases.\");");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();

        sb.AppendLine($"        public {model.TypeName}? Deserialize(in ReadOnlySequence<byte> buffer)");
        sb.AppendLine("        {");
        sb.AppendLine("            var reader = new SequenceReader<byte>(buffer);");
        sb.AppendLine("            var discriminator = __ReadDiscriminator(ref reader);");
        sb.AppendLine("            if (discriminator == 0)");
        sb.AppendLine("            {");
        sb.AppendLine("                RpcGeneratedCodecWire.EnsureFullyConsumed(reader);");
        sb.AppendLine("                return null;");
        sb.AppendLine("            }");
        sb.AppendLine("            switch (discriminator)");
        sb.AppendLine("            {");
        for (var index = 0; index < cases.Length; index++)
        {
            var discriminator = checked((int)cases[index].FieldId);
            sb.AppendLine($"                case {discriminator.ToString(InvariantCulture)}:");
            sb.AppendLine("                {");
            sb.AppendLine($"                    var decoded = __codec_{index}.Deserialize(reader.Sequence.Slice(reader.Position));");
            if (cases[index].Nullable)
            {
                sb.AppendLine("                    if (decoded is null)");
                sb.AppendLine($"                        throw RpcGeneratedCodecWire.DataLoss(\"Union '{EscapeString(model.TypeName)}' case {discriminator.ToString(InvariantCulture)} decoded a null concrete value.\");");
            }
            sb.AppendLine("                    return decoded;");
            sb.AppendLine("                }");
        }
        sb.AppendLine("                default:");
        sb.AppendLine($"                    throw RpcGeneratedCodecWire.DataLoss($\"Union '{EscapeString(model.TypeName)}' contains unknown discriminator {{discriminator}}.\");");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();

        sb.AppendLine($"        public bool TryGetEncodedSize(in {model.TypeName} value, out int size)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (value is null)");
        sb.AppendLine("            { size = sizeof(int); return true; }");
        sb.AppendLine("            switch (value)");
        sb.AppendLine("            {");
        for (var index = 0; index < cases.Length; index++)
        {
            var caseType = cases[index].TypeName;
            sb.AppendLine($"                case {caseType} __case_{index}:");
            if (TryGetStaticGeneratedCodecCoreType(caseType, concreteCodecTypes, out _))
            {
                sb.AppendLine($"                    if (!__codec_{index}.CanExactSize || !__codec_{index}.TryGetEncodedSize(__case_{index}, out var __nestedSize_{index}))");
            }
            else
            {
                sb.AppendLine($"                    var __sized_{index} = __sizedCodec_{index};");
                sb.AppendLine($"                    if (__sized_{index} is null || !__sized_{index}.CanExactSize || !__sized_{index}.TryGetEncodedSize(__case_{index}, out var __nestedSize_{index}))");
            }
            sb.AppendLine("                    { size = 0; return false; }");
            sb.AppendLine($"                    size = checked(sizeof(int) + __nestedSize_{index});");
            sb.AppendLine("                    return true;");
        }
        sb.AppendLine("                default:");
        sb.AppendLine($"                    throw new SharpLinkException(SharpLinkErrorCode.InvalidArgument, \"Union '{EscapeString(model.TypeName)}' received a runtime value that is not one of its declared cases.\");");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();

        sb.AppendLine($"        public bool TryGetEncodedSize(in {model.TypeName} value, out int size, out IRpcSizedCodecSnapshot? snapshot)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (value is null)");
        sb.AppendLine("            { size = sizeof(int); snapshot = null; return true; }");
        sb.AppendLine("            size = 0;");
        sb.AppendLine("            snapshot = null;");
        sb.AppendLine("            var __snapshot = RentSnapshot();");
        sb.AppendLine("            var __keepSnapshot = false;");
        sb.AppendLine("            __snapshot.__value = value;");
        sb.AppendLine("            try");
        sb.AppendLine("            {");
        sb.AppendLine("                switch (value)");
        sb.AppendLine("                {");
        for (var index = 0; index < cases.Length; index++)
        {
            var caseType = cases[index].TypeName;
            var discriminator = checked((int)cases[index].FieldId);
            sb.AppendLine($"                    case {caseType} __case_{index}:");
            sb.AppendLine($"                        __snapshot.__discriminator = {discriminator.ToString(InvariantCulture)};");
            if (TryGetStaticGeneratedCodecCoreType(caseType, concreteCodecTypes, out _))
            {
                sb.AppendLine($"                        if (!__codec_{index}.CanExactSize || !__codec_{index}.TryGetEncodedSize(__case_{index}, out __snapshot.__nestedSize, out __snapshot.__nestedSnapshot))");
            }
            else
            {
                sb.AppendLine($"                        var __sized_{index} = __sizedCodec_{index};");
                sb.AppendLine($"                        if (__sized_{index} is null || !__sized_{index}.CanExactSize || !__sized_{index}.TryGetEncodedSize(__case_{index}, out __snapshot.__nestedSize, out __snapshot.__nestedSnapshot))");
            }
            sb.AppendLine("                            return false;");
            sb.AppendLine("                        break;");
        }
        sb.AppendLine("                    default:");
        sb.AppendLine($"                        throw new SharpLinkException(SharpLinkErrorCode.InvalidArgument, \"Union '{EscapeString(model.TypeName)}' received a runtime value that is not one of its declared cases.\");");
        sb.AppendLine("                }");
        sb.AppendLine("                size = checked(sizeof(int) + __snapshot.__nestedSize);");
        sb.AppendLine("                snapshot = __snapshot;");
        sb.AppendLine("                __keepSnapshot = true;");
        sb.AppendLine("                return true;");
        sb.AppendLine("            }");
        sb.AppendLine("            finally");
        sb.AppendLine("            {");
        sb.AppendLine("                if (!__keepSnapshot)");
        sb.AppendLine("                {");
        sb.AppendLine("                    try { ReleaseCapturedChild(__snapshot); }");
        sb.AppendLine("                    finally { ReturnSnapshot(__snapshot); }");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();

        sb.AppendLine("        private void ReleaseCapturedChild(__SizedSnapshot snapshot)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (snapshot.__nestedSnapshot is null) return;");
        sb.AppendLine("            switch (snapshot.__discriminator)");
        sb.AppendLine("            {");
        for (var index = 0; index < cases.Length; index++)
        {
            var caseType = cases[index].TypeName;
            var discriminator = checked((int)cases[index].FieldId);
            sb.AppendLine($"                case {discriminator.ToString(InvariantCulture)}:");
            if (TryGetStaticGeneratedCodecCoreType(caseType, concreteCodecTypes, out _))
                sb.AppendLine($"                    __codec_{index}.ReleaseSnapshot(snapshot.__nestedSnapshot);");
            else
                sb.AppendLine($"                    __sizedCodec_{index}?.ReleaseSnapshot(snapshot.__nestedSnapshot);");
            sb.AppendLine("                    break;");
        }
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();

        sb.AppendLine($"        public void SerializeSized(in {model.TypeName} value, IBufferWriter<byte> buffer, int size, IRpcSizedCodecSnapshot? snapshot)");
        sb.AppendLine("        {");
        sb.AppendLine("            ArgumentNullException.ThrowIfNull(buffer);");
        sb.AppendLine("            if (value is null)");
        sb.AppendLine("            { __WriteDiscriminator(buffer, 0); return; }");
        sb.AppendLine("            if (snapshot is not __SizedSnapshot __snapshot)");
        sb.AppendLine("                throw new ArgumentException(\"Generated union sized serialization requires its captured snapshot.\", nameof(snapshot));");
        sb.AppendLine("            __WriteDiscriminator(buffer, __snapshot.__discriminator);");
        sb.AppendLine("            switch (__snapshot.__discriminator)");
        sb.AppendLine("            {");
        for (var index = 0; index < cases.Length; index++)
        {
            var caseType = cases[index].TypeName;
            var discriminator = checked((int)cases[index].FieldId);
            sb.AppendLine($"                case {discriminator.ToString(InvariantCulture)}:");
            if (TryGetStaticGeneratedCodecCoreType(caseType, concreteCodecTypes, out _))
                sb.AppendLine($"                    __codec_{index}.SerializeSized(({caseType})__snapshot.__value!, buffer, __snapshot.__nestedSize, __snapshot.__nestedSnapshot);");
            else
                sb.AppendLine($"                    (__sizedCodec_{index} ?? throw new InvalidOperationException(\"Generated union exact-size capability changed after binding.\")).SerializeSized(({caseType})__snapshot.__value!, buffer, __snapshot.__nestedSize, __snapshot.__nestedSnapshot);");
            sb.AppendLine("                    return;");
        }
        sb.AppendLine("                default:");
        sb.AppendLine("                    throw new InvalidOperationException(\"Generated union sized snapshot has an invalid discriminator.\");");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();

        sb.AppendLine("        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (snapshot is not __SizedSnapshot __snapshot) return;");
        sb.AppendLine("            try { ReleaseCapturedChild(__snapshot); }");
        sb.AppendLine("            finally { ReturnSnapshot(__snapshot); }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }

}
