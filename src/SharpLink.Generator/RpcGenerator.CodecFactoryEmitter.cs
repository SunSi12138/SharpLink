namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static void AppendAdapterCodecHolders(
        StringBuilder sb,
        ImmutableArray<GeneratedCodecModel> codecs)
    {
        foreach (var adapter in codecs
                     .Where(static codec => codec.Kind == GeneratedCodecKind.Adapter)
                     .GroupBy(static codec => codec.AdapterId, StringComparer.Ordinal)
                     .Select(static group => group.First())
                     .OrderBy(static codec => codec.AdapterId, StringComparer.Ordinal))
        {
            sb.AppendLine($"internal static class {GetAdapterHolderName(adapter.AdapterId!)}");
            sb.AppendLine("{");
            sb.AppendLine($"    internal static readonly IRpcCodecAdapter Instance = new {adapter.AdapterType}();");
            sb.AppendLine("}");
            sb.AppendLine();
        }
    }

    private static void AppendCustomCodecFactory(StringBuilder sb, GeneratedCodecModel model)
    {
        sb.AppendLine($"internal static class {model.CodecName}");
        sb.AppendLine("{");
        sb.AppendLine("    internal sealed class Factory : IRpcGeneratedCodecFactory");
        sb.AppendLine("    {");
        sb.AppendLine($"        public Type TargetType => typeof({model.TypeName});");
        AppendFactoryCodecHash(sb, model);
        sb.AppendLine("        public string? AdapterId => null;");
        sb.AppendLine("        public IRpcCodecAdapter? Adapter => null;");
        sb.AppendLine("        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)");
        sb.AppendLine("        {");
        sb.AppendLine("            ArgumentNullException.ThrowIfNull(provider);");
        sb.AppendLine("            if (adapterScope is not null)");
        sb.AppendLine("                throw new ArgumentException(\"Custom Codec factories do not accept an adapter scope.\", nameof(adapterScope));");
        sb.AppendLine($"            return new {model.CustomCodecType}();");
        sb.AppendLine("        }");
        sb.AppendLine($"        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<{model.TypeName}>;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void AppendAdapterCodecFactory(StringBuilder sb, GeneratedCodecModel model)
    {
        sb.AppendLine($"internal static class {model.CodecName}");
        sb.AppendLine("{");
        sb.AppendLine("    internal sealed class Factory : IRpcGeneratedCodecFactory");
        sb.AppendLine("    {");
        sb.AppendLine($"        public Type TargetType => typeof({model.TypeName});");
        AppendFactoryCodecHash(sb, model);
        sb.AppendLine($"        public string? AdapterId => \"{EscapeString(model.AdapterId!)}\";");
        sb.AppendLine($"        public IRpcCodecAdapter Adapter => {GetAdapterHolderName(model.AdapterId!)}.Instance;");
        sb.AppendLine("        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)");
        sb.AppendLine("        {");
        sb.AppendLine("            ArgumentNullException.ThrowIfNull(provider);");
        sb.AppendLine("            ArgumentNullException.ThrowIfNull(adapterScope);");
        if (IsSharpPackAdapter(model))
            sb.AppendLine("            __SharpLinkGeneratedSharpPackIntegration.Configure(adapterScope);");
        sb.AppendLine($"            return adapterScope.CreateCodec<{model.TypeName}>();");
        sb.AppendLine("        }");
        sb.AppendLine($"        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<{model.TypeName}>;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static string GetAdapterHolderName(string adapterId)
        => "__SharpLinkGeneratedAdapter_" + ComputeEmitterHash(adapterId).ToString("X16", InvariantCulture);

    private static ulong ComputeEmitterHash(string value)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= prime;
        }
        return hash;
    }

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
