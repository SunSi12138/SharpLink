namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static void AppendFactoryCodecHash(StringBuilder sb, GeneratedCodecModel model)
        => AppendFactoryCodecHash(sb, model.CodecHashHigh, model.CodecHashLow);

    private static void AppendFactoryCodecHash(StringBuilder sb, ulong codecHashHigh, ulong codecHashLow)
        => sb.AppendLine($"        public RpcHash128 CodecHash => new(0x{codecHashHigh.ToString("x16", InvariantCulture)}UL, 0x{codecHashLow.ToString("x16", InvariantCulture)}UL);");

    private static void AppendFactory(StringBuilder sb, GeneratedCodecModel model)
        => AppendFactory(sb, model.TypeName, model.CodecName, model.CodecHashHigh, model.CodecHashLow);

    private static void AppendDtoFactory(StringBuilder sb, DtoCodecAnalysisModel model)
        => AppendFactory(sb, model.TypeName, model.CodecName, model.CodecHashHigh, model.CodecHashLow);

    private static void AppendFactory(
        StringBuilder sb,
        string typeName,
        string codecName,
        ulong codecHashHigh,
        ulong codecHashLow)
    {
        sb.AppendLine();
        sb.AppendLine("    internal sealed class Factory : IRpcGeneratedCodecFactory");
        sb.AppendLine("    {");
        sb.AppendLine($"        public Type TargetType => typeof({typeName});");
        AppendFactoryCodecHash(sb, codecHashHigh, codecHashLow);
        sb.AppendLine("        public string? AdapterId => null;");
        sb.AppendLine("        public IRpcCodecAdapter? Adapter => null;");
        sb.AppendLine($"        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (adapterScope is not null)");
        sb.AppendLine("                throw new ArgumentException(\"Native Codec factories do not accept an adapter scope.\", nameof(adapterScope));");
        sb.AppendLine($"            return new {codecName}(provider);");
        sb.AppendLine("        }");
        sb.AppendLine($"        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<{typeName}>;");
        sb.AppendLine("    }");
    }

}
