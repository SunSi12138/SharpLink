using System.Collections.Generic;

namespace SharpLink.CodecCompatibility;

internal sealed class CorpusEnvelope
{
    public int SchemaVersion { get; set; } = 1;
    public RuntimeManifest Manifest { get; set; } = new();
    public Dictionary<string, string> CaseBytesBase64 { get; set; } = [];
}
