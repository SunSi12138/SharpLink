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
        AppendDtoStaticCore(
            sb,
            model,
            complexMembers,
            complexIndexes,
            hasDirectString,
            hasComplex,
            concreteCodecTypes);
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
        // Keep async/pending-operation state bounded while preserving the concrete child
        // Codec graph for common generated DTOs. Store concrete sealed child Codec references
        // rather than recursively embedding child Cores so nested DTO depth does not inflate
        // the Core exponentially. Larger fan-out graphs keep the authoritative owner-only Core.
        const int MaxConcreteChildCodecFields = 4;
        var useHybridCore = complexMembers.Length <= MaxConcreteChildCodecFields;

        sb.AppendLine();
        sb.AppendLine("    internal Core StaticCore => new(this);");
        sb.AppendLine();
        sb.AppendLine($"    internal readonly struct Core : IRpcCodec<{model.TypeName}>, IRpcSizedCodec<{model.TypeName}>");
        sb.AppendLine("    {");
        sb.AppendLine($"        private readonly {model.CodecName} __owner;");

        if (!useHybridCore)
        {
            sb.AppendLine();
            sb.AppendLine($"        internal Core({model.CodecName} owner) => __owner = owner;");
            sb.AppendLine();
            sb.AppendLine("        public bool CanExactSize => __owner.CanExactSize;");
            sb.AppendLine();
            sb.AppendLine($"        public void Serialize(in {model.TypeName} value, IBufferWriter<byte> writer)");
            sb.AppendLine("            => __owner.Serialize(in value, writer);");
            sb.AppendLine();
            var ownerReturnType = model.IsReferenceType ? model.TypeName + "?" : model.TypeName;
            sb.AppendLine($"        public {ownerReturnType} Deserialize(in ReadOnlySequence<byte> buffer)");
            sb.AppendLine("            => __owner.Deserialize(in buffer);");
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
            return;
        }

        for (var index = 0; index < complexMembers.Length; index++)
        {
            var member = complexMembers[index];
            var childStorageType = GetCodecStorageType(
                member.TypeName,
                member.CodecLookupTypeName,
                concreteCodecTypes);
            sb.AppendLine($"        private readonly {childStorageType} __codec_{index};");
            if (!member.Nullable &&
                TryGetStaticGeneratedCodecCoreType(
                    member.CodecLookupTypeName,
                    concreteCodecTypes,
                    out _))
            {
                // For non-nullable statically generated children the concrete class already
                // implements the exact IRpcSizedCodec<T> shape. Nullable members retain the
                // interface-typed sizing field so nullable annotations are preserved.
                sb.AppendLine($"        private {childStorageType} __sizedCodec_{index} => __codec_{index};");
            }
            else
            {
                sb.AppendLine(
                    $"        private readonly IRpcSizedCodec<{member.TypeName}>? __sizedCodec_{index};");
            }
        }
        if (complexMembers.Length != 0)
            sb.AppendLine("        private readonly bool __canExactSize;");
        sb.AppendLine();
        sb.AppendLine($"        internal Core({model.CodecName} owner)");
        sb.AppendLine("        {");
        sb.AppendLine("            __owner = owner;");
        for (var index = 0; index < complexMembers.Length; index++)
        {
            var member = complexMembers[index];
            sb.AppendLine($"            __codec_{index} = owner.__codec_{index};");
            if (member.Nullable ||
                !TryGetStaticGeneratedCodecCoreType(
                    member.CodecLookupTypeName,
                    concreteCodecTypes,
                    out _))
            {
                sb.AppendLine($"            __sizedCodec_{index} = owner.__sizedCodec_{index};");
            }
        }
        if (complexMembers.Length != 0)
            sb.AppendLine("            __canExactSize = owner.__canExactSize;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        private __SizedSnapshot RentSnapshot() => __owner.RentSnapshot();");
        sb.AppendLine("        private void ReturnSnapshot(__SizedSnapshot snapshot) => __owner.ReturnSnapshot(snapshot);");
        sb.AppendLine();
        sb.AppendLine(complexMembers.Length == 0
            ? "        public bool CanExactSize => true;"
            : "        public bool CanExactSize => __canExactSize;");
        sb.AppendLine();

        var coreMethods = new StringBuilder();
        AppendDtoSerializeMethod(coreMethods, model, complexIndexes, hasDirectString, hasComplex);
        coreMethods.AppendLine();
        AppendDtoEncodedSizeMethod(coreMethods, model, complexIndexes, appendSnapshotType: false);
        coreMethods.AppendLine();
        var coreReturnType = model.IsReferenceType ? model.TypeName + "?" : model.TypeName;
        coreMethods.AppendLine($"    public {coreReturnType} Deserialize(in ReadOnlySequence<byte> buffer)");
        coreMethods.AppendLine("        => __owner.Deserialize(in buffer);");
        sb.Append(Indent(coreMethods.ToString(), "    "));
        sb.AppendLine("    }");
    }
}
