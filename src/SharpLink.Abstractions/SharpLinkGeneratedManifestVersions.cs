namespace SharpLink.Abstractions;

/// <summary>Defines generated manifest compatibility constants for the current SharpLink release line.</summary>
#if SHARPLINK_GENERATOR
internal
#else
public
#endif
static class SharpLinkGeneratedManifestVersions
{
    /// <summary>
    /// The 3.0 generated API requires identity-bearing contract channels, complete method facts,
    /// and owner-local UnsafeBlit factories. Regenerate every
    /// generated assembly with the matching SDK and exact ABI identity; the API number alone
    /// does not identify compatible development branches.
    /// </summary>
    public const int Api = 5;

    /// <summary>Exact discriminator for the 3.0/API5 contract-channel and complete-method-facts ABI.</summary>
    public const string AbiIdentity = "sharplink-3.0-api5-contract-channel-complete-method-facts-v1";

    /// <summary>The exact binding-aware SharpLink wire protocol major version.</summary>
    public const int Protocol = 3;
}
