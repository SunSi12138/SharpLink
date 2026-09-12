namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private static string GetContractArtifactIdentity(RpcInterfaceModel contract)
        => unchecked((ulong)contract.Hash).ToString("X16", InvariantCulture);

    private static string GetHelperTypeReference(RpcInterfaceModel contract, string typeName)
        => string.IsNullOrEmpty(contract.Namespace)
            ? $"global::{typeName}"
            : $"global::{contract.Namespace}.{typeName}";

    private static string Indent(string value, string indent)
    {
        var normalized = value.Replace("\r\n", "\n");
        var sb = new StringBuilder(normalized.Length + (indent.Length * 16));
        var start = 0;
        while (start < normalized.Length)
        {
            var newLine = normalized.IndexOf('\n', start);
            var end = newLine == -1 ? normalized.Length : newLine + 1;
            var line = normalized.Substring(start, end - start);
            if (line.Length != 0 && !string.Equals(line, "\n", StringComparison.Ordinal))
                sb.Append(indent);
            sb.Append(line);
            start = end;
        }
        return sb.ToString();
    }
}
