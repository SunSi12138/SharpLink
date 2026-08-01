namespace SharpLink.Server;

/// <summary>Builds an independently owned SharpLink RPC server.</summary>
public interface ISharpLinkServerBuilder
{
    /// <summary>Validates configuration, freezes generated services, and creates the server.</summary>
    /// <returns>A server that owns its configured transport and internal runtime resources.</returns>
    ISharpLinkServer Build();
}
