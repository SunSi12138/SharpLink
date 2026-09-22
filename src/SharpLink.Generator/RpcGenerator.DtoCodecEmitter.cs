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
            sb.AppendLine($"        if (__sizedCodec_{index} is not {{ CanExactSize: true }})");
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
        sb.AppendLine("}");
        sb.AppendLine();
    }
}
