namespace SharpLink.Benchmarks;

// The collector must stamp these after verifying the actual worktree sources.
// MeasurementSourceSha must hash this footprint project, not the separate RPC harness.
internal static class ModeIdentity
{
    internal const string Mode = "UNCONFIGURED";
    internal const string BaselineSha = "UNCONFIGURED";
    internal const string CandidateSha = "UNCONFIGURED";
    internal const string RuntimeSourceTreeSha = "UNCONFIGURED";
    internal const string MeasurementSourceSha = "UNCONFIGURED";
    internal const string ControllerSourceSha256 = "UNCONFIGURED";
}

