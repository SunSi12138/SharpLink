namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static void AppendDtoCodec(
        StringBuilder sb,
        DtoCodecAnalysisModel model,
        IReadOnlyDictionary<string, string> concreteCodecTypes)
    {
        var complexMembers = model.Members
            .Where(static member => member.Kind == GeneratedMemberKind.Complex)
            .ToArray();
        var hasDirectString = model.Members.Any(static member => member.Kind == GeneratedMemberKind.String);
        var hasComplex = complexMembers.Length != 0;
        var complexIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < complexMembers.Length; index++)
            complexIndexes.Add(complexMembers[index].Name, index);

        sb.AppendLine($"internal sealed class {model.CodecName} : IRpcCodec<{model.TypeName}>, IRpcSizedCodec<{model.TypeName}>");
        sb.AppendLine("{");
        sb.AppendLine("    private readonly global::System.Collections.Concurrent.ConcurrentBag<__SizedSnapshot> __snapshotPool = new();");
        sb.AppendLine();
        sb.AppendLine("    private __SizedSnapshot RentSnapshot()");
        sb.AppendLine("    {");
        sb.AppendLine("        if (__snapshotPool.TryTake(out var pooled))");
        sb.AppendLine("            return pooled;");
        sb.AppendLine("        return new __SizedSnapshot();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private void ReturnSnapshot(__SizedSnapshot snapshot)");
        sb.AppendLine("    {");
        sb.AppendLine("        snapshot.Clear();");
        sb.AppendLine("        __snapshotPool.Add(snapshot);");
        sb.AppendLine("    }");
        sb.AppendLine();
        for (var index = 0; index < complexMembers.Length; index++)
        {
            var member = complexMembers[index];
            sb.AppendLine(
                $"    private readonly {GetCodecStorageType(member.TypeName, member.CodecLookupTypeName, concreteCodecTypes)} __codec_{index};");
            sb.AppendLine(
                $"    private readonly IRpcSizedCodec<{member.TypeName}>? __sizedCodec_{index};");
        }
        sb.AppendLine("    private readonly bool __canExactSize;");
        sb.AppendLine();
        sb.AppendLine($"    internal {model.CodecName}(IRpcCodecProvider provider)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(provider);");
        for (var index = 0; index < complexMembers.Length; index++)
        {
            var member = complexMembers[index];
            sb.AppendLine(
                $"        __codec_{index} = {GetCodecResolveExpression("provider", member.TypeName, member.CodecLookupTypeName, concreteCodecTypes)};");
            sb.AppendLine(
                $"        __sizedCodec_{index} = (object)__codec_{index} as IRpcSizedCodec<{member.TypeName}>;");
        }
        sb.AppendLine("        __canExactSize = true;");
        for (var index = 0; index < complexMembers.Length; index++)
        {
            sb.AppendLine($"        if (__sizedCodec_{index} is null || !__sizedCodec_{index}.CanExactSize)");
            sb.AppendLine("            __canExactSize = false;");
        }
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public bool CanExactSize => __canExactSize;");
        sb.AppendLine();
        AppendDtoSerializeMethod(sb, model, complexIndexes, hasDirectString, hasComplex);
        sb.AppendLine();
        AppendDtoEncodedSizeMethod(sb, model, complexIndexes);
        sb.AppendLine();
        AppendDtoDeserializeMethod(sb, model, complexIndexes);
        AppendDtoFactory(sb, model);
        AppendDtoStaticCore(sb, model, complexMembers, complexIndexes, hasDirectString, hasComplex, concreteCodecTypes);
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void AppendDtoStaticCore(
        StringBuilder sb,
        DtoCodecAnalysisModel model,
        DtoMemberAnalysisModel[] complexMembers,
        Dictionary<string, int> complexIndexes,
        bool hasDirectString,
        bool hasComplex,
        IReadOnlyDictionary<string, string> concreteCodecTypes)
    {
        sb.AppendLine();
        sb.AppendLine("    internal Core StaticCore => new(this);");
        sb.AppendLine();
        sb.AppendLine($"    internal readonly struct Core : IRpcCodec<{model.TypeName}>, IRpcSizedCodec<{model.TypeName}>");
        sb.AppendLine("    {");
        sb.AppendLine($"        private readonly {model.CodecName} __owner;");
        for (var index = 0; index < complexMembers.Length; index++)
        {
            var member = complexMembers[index];
            sb.AppendLine(
                $"        private readonly {GetCodecHotStorageType(member.TypeName, member.CodecLookupTypeName, concreteCodecTypes)} __codec_{index};");
        }
        sb.AppendLine("        private readonly bool __canExactSize;");
        sb.AppendLine();
        sb.AppendLine($"        internal Core({model.CodecName} owner)");
        sb.AppendLine("        {");
        sb.AppendLine("            __owner = owner;");
        for (var index = 0; index < complexMembers.Length; index++)
        {
            var member = complexMembers[index];
            sb.AppendLine(
                $"            __codec_{index} = {GetCodecHotBoundExpression($"owner.__codec_{index}", member.CodecLookupTypeName, concreteCodecTypes)};");
        }
        sb.AppendLine("            __canExactSize = owner.__canExactSize;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        public bool CanExactSize => __canExactSize;");
        sb.AppendLine();

        var serialize = new StringBuilder();
        AppendDtoSerializeMethod(serialize, model, complexIndexes, hasDirectString, hasComplex);
        var deserialize = new StringBuilder();
        AppendDtoDeserializeMethod(deserialize, model, complexIndexes);
        sb.Append(Indent(serialize.ToString(), "    "));
        sb.AppendLine();
        sb.Append(Indent(deserialize.ToString(), "    "));
        sb.AppendLine();
        sb.AppendLine($"        public bool TryGetEncodedSize(in {model.TypeName} value, out int size)");
        sb.AppendLine("            => __owner.TryGetEncodedSize(in value, out size);");
        sb.AppendLine();
        sb.AppendLine($"        public bool TryGetEncodedSize(in {model.TypeName} value, out int size, out IRpcSizedCodecSnapshot? snapshot)");
        sb.AppendLine("            => __owner.TryGetEncodedSize(in value, out size, out snapshot);");
        sb.AppendLine();
        sb.AppendLine($"        public void SerializeSized(in {model.TypeName} value, IBufferWriter<byte> buffer, int size, IRpcSizedCodecSnapshot? snapshot)");
        sb.AppendLine("            => __owner.SerializeSized(in value, buffer, size, snapshot);");
        sb.AppendLine();
        sb.AppendLine("        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot)");
        sb.AppendLine("            => __owner.ReleaseSnapshot(snapshot);");
        sb.AppendLine("    }");
    }
}
