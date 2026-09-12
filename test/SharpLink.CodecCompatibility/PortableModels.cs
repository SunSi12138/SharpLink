using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SharpLink.CodecCompatibility;

internal sealed class CorpusEnvelope
{
    [JsonRequired]
    public int SchemaVersion { get; set; }
    public RuntimeManifest Manifest { get; set; } = new();
    public Dictionary<string, string> CaseBytesBase64 { get; set; } = [];
}
