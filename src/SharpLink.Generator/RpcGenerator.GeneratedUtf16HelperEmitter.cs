namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static void AppendGeneratedUtf16Helper(StringBuilder sb)
    {
        sb.AppendLine("internal static class __SharpLinkGeneratedUtf16");
        sb.AppendLine("{");
        sb.AppendLine("    internal static int GetByteCount(string value) => checked(value.Length * sizeof(char));");
        sb.AppendLine();
        sb.AppendLine("    internal static void WriteStringKnownSize(IBufferWriter<byte> writer, string value, int byteCount)");
        sb.AppendLine("    {");
        sb.AppendLine("        var length = writer.GetSpan(sizeof(int));");
        sb.AppendLine("        global::System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, byteCount);");
        sb.AppendLine("        writer.Advance(sizeof(int));");
        sb.AppendLine("        if (byteCount == 0)");
        sb.AppendLine("            return;");
        sb.AppendLine("        var payload = writer.GetSpan(byteCount);");
        sb.AppendLine("        value.AsSpan().CopyTo(global::System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(payload));");
        sb.AppendLine("        writer.Advance(byteCount);");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
    }
}
