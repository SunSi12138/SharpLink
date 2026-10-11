namespace SharpLink.Benchmarks;

// The runner verifies both subjects before stamping these compile-time constants.
// UNCONFIGURED is deliberately not runnable evidence.
internal static class ModeIdentity
{
    internal const string Mode = "UNCONFIGURED";
    internal const string BaselineSha = "UNCONFIGURED";
    internal const string SubjectSha = "UNCONFIGURED";
    internal const string SubjectCommitTreeSha = "UNCONFIGURED";
    internal const string RuntimeSourceTreeSha = "UNCONFIGURED";
    internal const string RuntimeSourceContentSha256 = "UNCONFIGURED";
    internal const string MeasurementRecipeSha = "UNCONFIGURED";
    internal const string MeasurementSourceSha256 = "UNCONFIGURED";
    internal const string ControllerSourceSha256 = "UNCONFIGURED";
    internal const string GrantSourceSha256 = "UNCONFIGURED";
}
