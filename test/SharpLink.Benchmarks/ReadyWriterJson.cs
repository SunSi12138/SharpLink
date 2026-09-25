#if SHARPLINK_READY_WRITER_EXPERIMENT
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SharpLink.Benchmarks;

// Preserve the existing report field names while removing reflection-only serialization.
internal sealed record ReadyWriterRunMetadata
{
    public required string Source { get; init; }
    public required string Runtime { get; init; }
    public required string OS { get; init; }
    public string? Pgo { get; init; }
    public bool DynamicCodeSupported { get; init; }
    public bool DiagnosticCapture { get; init; }
    public int ProcessorCount { get; init; }
    public required string transport { get; init; }
    public int streams { get; init; }
    public int items { get; init; }
    public int bytes { get; init; }
    public int rounds { get; init; }
    public int connection { get; init; }
    public int slots { get; init; }
    public int flush { get; init; }
    public int orderOffset { get; init; }
    public int preparedByteBudget { get; init; }
    public bool allocationDiagnostic { get; init; }
    public required string Scope { get; init; }
}

internal sealed record ReadyWriterReport(ReadyWriterRunMetadata metadata, string status,
    string? error, List<PhaseBTransportEvidenceRunner.Sample> samples);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ReadyWriterReport))]
internal partial class ReadyWriterJsonContext : JsonSerializerContext { }
#endif
