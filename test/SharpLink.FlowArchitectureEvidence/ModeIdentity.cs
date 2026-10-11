namespace SharpLink.Benchmarks;

// The collector MUST replace this file in each isolated source tree before building.
// CLI arguments only verify these compiled constants. They cannot relabel an existing binary.
// RuntimeSourceTreeSha is a collector-computed hash/tree identity for the actual compiled src/.
// MeasurementSourceSha identifies the shared harness source, excluding this generated identity.
internal static class ModeIdentity
{
    internal const string Mode = "baseline";
    internal const string BaselineSha = "UNCONFIGURED";
    internal const string CandidateSha = "UNCONFIGURED";
    internal const string RuntimeSourceTreeSha = "UNCONFIGURED";
    internal const string MeasurementSourceSha = "UNCONFIGURED";
    internal const string ControllerSourceSha256 = "UNCONFIGURED";
    internal const string SendPumpSourceSha256 = "UNCONFIGURED";
}

