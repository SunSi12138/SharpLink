namespace SharpLink.Sdk;

/// <summary>Assigns a stable wire tag to one concrete case of a polymorphic RPC contract type.</summary>
/// <example><code>[RpcUnionCase(1, typeof(CardPayment))]</code></example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = true, Inherited = false)]
public sealed class RpcUnionCaseAttribute : Attribute
{
    /// <summary>Creates a union case mapping.</summary>
    /// <param name="tag">A positive tag that remains assigned to the same concrete type.</param>
    /// <param name="caseType">The concrete RPC payload type.</param>
    public RpcUnionCaseAttribute(int tag, Type caseType)
    {
        Tag = tag;
        CaseType = caseType ?? throw new ArgumentNullException(nameof(caseType));
    }

    /// <summary>Gets the stable positive wire tag.</summary>
    public int Tag { get; }

    /// <summary>Gets the concrete type assigned to <see cref="Tag"/>.</summary>
    public Type CaseType { get; }
}
