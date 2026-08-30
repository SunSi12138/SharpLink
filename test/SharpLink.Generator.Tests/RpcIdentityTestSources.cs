using System;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    private static string UseCurrentIdentitySdk(string source)
    {
        source = source.Replace(
            "public RpcCodecAdapterRegistrationAttribute(Type adapterType, string adapterId, string wireFormatId) { }",
            "public RpcCodecAdapterRegistrationAttribute(Type adapterType, string adapterId) { }",
            StringComparison.Ordinal);
        return source.Replace(
            """
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class RpcCodecImplementationAttribute : Attribute
    {
        public RpcCodecImplementationAttribute(string wireFormatId, string schemaId) { }
    }
""",
            """
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    public sealed class RpcCodecSemanticIdentityAttribute : Attribute
    {
        public RpcCodecSemanticIdentityAttribute(ulong high, ulong low) { }
    }
""",
            StringComparison.Ordinal);
    }
}
