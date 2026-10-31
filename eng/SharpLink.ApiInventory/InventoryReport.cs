using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class InventoryReport
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    internal static readonly JsonSerializerOptions JsonLine = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));
    internal static string FileHash(string path) => Hash(File.ReadAllBytes(path));
    internal static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    internal static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json) + "\n");
    internal static void WriteLines<T>(string path, IEnumerable<T> values) => File.WriteAllLines(path,
        values.Select(value => JsonSerializer.Serialize(value, JsonLine)), new UTF8Encoding(false));
}

internal sealed record DocumentEvidence(string Path, string Kind, string Sha256, string? HintName, string? GeneratorAssembly, string? GeneratorType, string? SnapshotPath);
internal sealed record SourceEvidence(string Path, int StartLine, int EndLine, int Start, int Length, string Sha256, bool Generated);
internal sealed record Relation(string Source, string Kind, string Target, string TargetAssembly, string TargetAccessibility);

internal sealed record SymbolRow
{
    public int SchemaVersion => 1;
    public required string Id { get; init; }
    public required string SemanticId { get; init; }
    public required string Project { get; init; }
    public required string Framework { get; init; }
    public required string Assembly { get; init; }
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public required string MetadataName { get; init; }
    public required string Signature { get; init; }
    public required string DeclaredAccessibility { get; init; }
    public required string EffectiveAccessibility { get; init; }
    public required string[] AccessibilityConstraints { get; init; }
    public required string Origin { get; init; }
    public required bool IsImplicit { get; init; }
    public required bool HasGeneratedSource { get; init; }
    public required bool ExternallyVisible { get; init; }
    public required bool NonSealedClass { get; init; }
    public required bool HasPublicInstanceConstructor { get; init; }
    public required string? AssociatedSymbol { get; init; }
    public required string? ContainingSymbol { get; init; }
    public required Dictionary<string, object?> Details { get; init; }
    public required SourceEvidence[] Declarations { get; init; }
    public required SourceEvidence[] Locations { get; init; }
}
