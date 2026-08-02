namespace SharpLink.Sdk;

/// <summary>Assigns a stable positive wire field ID to a DTO field or property.</summary>
/// <example><code>[RpcMember(1)] public string Name { get; set; } = string.Empty;</code></example>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class RpcMemberAttribute : Attribute
{
    /// <summary>Creates a member mapping.</summary>
    /// <param name="id">A positive field ID no greater than 536,870,911.</param>
    public RpcMemberAttribute(int id)
    {
        Id = id;
    }

    /// <summary>Gets the configured wire field ID.</summary>
    public int Id { get; }
}
