namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static GeneratedCodecModel CreateUnsafeBlitFactoryModel(FinalUnsafeBlitCodecPlan plan)
        => new(
            plan.TypeName, "__SharpLinkGeneratedUnsafeBlit_" + Hashing.GetIdentifierHash(plan.TypeName), string.Empty,
            GeneratedCodecKind.UnsafeBlit, false, ImmutableArray<GeneratedMemberModel>.Empty,
            ImmutableArray<string>.Empty, null, null, null, null, null, null, string.Empty,
            ImmutableArray<string>.Empty, null)
        {
            UnsafeBlitRequirement = new GeneratedUnsafeBlitRequirementModel(
                plan.TypeName, plan.Abi.NativePointerWidth, RequiresDateTimeOffsetRawAbi(plan.Layout))
        };

    private static void AppendUnsafeBlitCodecFactory(StringBuilder sb, GeneratedCodecModel model)
    {
        var requirement = model.UnsafeBlitRequirement ?? throw new InvalidOperationException(
            "An UnsafeBlit factory must carry its finalized ABI requirement.");
        sb.AppendLine($"internal static class {model.CodecName}");
        sb.AppendLine("{");
        sb.AppendLine("    internal sealed class Factory : IRpcGeneratedUnsafeBlitCodecFactory");
        sb.AppendLine("    {");
        sb.AppendLine($"        public Type TargetType => typeof({model.TypeName});");
        sb.AppendLine($"        public SharpLinkGeneratedUnsafeBlitRequirement Requirement => new({requirement.NativePointerWidth.ToString(InvariantCulture)}, {(requirement.RequiresDateTimeOffsetRawAbi ? "true" : "false")});");
        AppendFactoryCodecHash(sb, model);
        sb.AppendLine("        public string? AdapterId => null;");
        sb.AppendLine("        public IRpcCodecAdapter? Adapter => null;");
        sb.AppendLine("        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (adapterScope is not null)");
        sb.AppendLine("                throw new ArgumentException(\"UnsafeBlit factories do not accept an adapter scope.\", nameof(adapterScope));");
        sb.AppendLine("            if (provider is not IRpcGeneratedUnsafeBlitCodecProvider generated)");
        sb.AppendLine("                throw new NotSupportedException(\"The provider cannot validate generated UnsafeBlit ABI requirements.\");");
        sb.AppendLine($"            return generated.GetGeneratedUnsafeBlitCodec<{model.TypeName}>(this, Requirement);");
        sb.AppendLine("        }");
        sb.AppendLine($"        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<{model.TypeName}>;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
    }
}
