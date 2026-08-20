using System;
using System.Collections.Generic;

namespace SharpLink.CodecCompatibility;

internal sealed class RuntimeManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string SharpLinkCommit { get; set; } = string.Empty;
    public string TargetFramework { get; set; } = string.Empty;
    public string FrameworkDescription { get; set; } = string.Empty;
    public string RuntimeFamily { get; set; } = string.Empty;
    public string RuntimeVersion { get; set; } = string.Empty;
    public string SdkVersion { get; set; } = string.Empty;
    public string RuntimeIdentifier { get; set; } = string.Empty;
    public string Os { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public string ProcessArchitecture { get; set; } = string.Empty;
    public string OsArchitecture { get; set; } = string.Empty;
    public int PointerSize { get; set; }
    public bool IsLittleEndian { get; set; }
    public string CompilationMode { get; set; } = string.Empty;
    public string PlatformTag { get; set; } = string.Empty;
    public List<CaseManifest> Cases { get; set; } = [];
    public List<PaddingPoisonResult> PaddingPoison { get; set; } = [];
}

internal sealed class CaseManifest
{
    public string Id { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string CodecPath { get; set; } = "UnsafeBlitDirect";
    public string Type { get; set; } = string.Empty;
    public bool NativeWidth { get; set; }
    public int Size { get; set; }
    public Dictionary<string, int> FieldOffsets { get; set; } = [];
    public string ExpectedLogicalValue { get; set; } = string.Empty;
    public string WireFile { get; set; } = string.Empty;
    public string WireSha256 { get; set; } = string.Empty;
}

internal sealed class PaddingPoisonResult
{
    public string Fixture { get; set; } = string.Empty;
    public int Size { get; set; }
    public bool LogicalValuesEqual { get; set; }
    public bool WireBytesEqual { get; set; }
    public List<int> DifferingByteOffsets { get; set; } = [];
    public List<int> PaddingByteOffsets { get; set; } = [];
    public bool DifferencesOnlyInPadding { get; set; }
    public string SourceAHash { get; set; } = string.Empty;
    public string SourceBHash { get; set; } = string.Empty;
    public string WireAHash { get; set; } = string.Empty;
    public string WireBHash { get; set; } = string.Empty;
}

internal sealed class VerificationReport
{
    public int SchemaVersion { get; set; } = 1;
    public RuntimeManifest Consumer { get; set; } = new();
    public List<VerificationEntry> Results { get; set; } = [];
}

internal sealed class VerificationEntry
{
    public string Producer { get; set; } = string.Empty;
    public string Consumer { get; set; } = string.Empty;
    public string Fixture { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string CodecPath { get; set; } = string.Empty;
    public int ProducerSize { get; set; }
    public int ConsumerSize { get; set; }
    public int ProducerPointerSize { get; set; }
    public int ConsumerPointerSize { get; set; }
    public Dictionary<string, int> ProducerFieldOffsets { get; set; } = [];
    public Dictionary<string, int> ConsumerFieldOffsets { get; set; } = [];
    public string ProducerWireHash { get; set; } = string.Empty;
    public string ConsumerLocalWireHash { get; set; } = string.Empty;
    public bool CrossDeserializeResult { get; set; }
    public bool LogicalEquality { get; set; }
    public bool ByteForByteEquality { get; set; }
    public int? FirstDifferingByteOffset { get; set; }
    public string Classification { get; set; } = string.Empty;
    public bool Blocking { get; set; }
    public string ExpectedLogicalValue { get; set; } = string.Empty;
    public string ActualLogicalValue { get; set; } = string.Empty;
    public string? ExceptionType { get; set; }
    public string? ExceptionMessage { get; set; }
}

internal sealed class CompatibilitySummary
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public int BlockingFailures { get; set; }
    public List<VerificationEntry> Results { get; set; } = [];
}
