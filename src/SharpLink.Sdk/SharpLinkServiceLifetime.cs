namespace SharpLink.Sdk;

/// <summary>Defines the lifetime of a root RPC service instance.</summary>
public enum SharpLinkServiceLifetime
{
    /// <summary>One instance is shared by every connection and call on a server registration.</summary>
    Singleton,

    /// <summary>One instance is shared by calls made over one authenticated physical connection.</summary>
    Connection,

    /// <summary>One instance is created for each invocation and retained for the complete stream.</summary>
    Call
}
