using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    private static readonly Regex LegacyAdapterRegistrationPattern = new(
        """RpcCodecAdapterRegistration\(typeof\((?<type>[^)]+)\),\s*"(?<id>[^"]*)",\s*"(?<wire>[^"]*)"(?<tail>\s*(?:,\s*SelectorAttributeType\s*=\s*typeof\([^)]+\))?)\)""",
        RegexOptions.CultureInvariant);
    private static readonly Regex LegacyCodecIdentityPattern = new(
        """(?<prefix>SharpLink\.Sdk\.)?RpcCodecImplementation\("(?<wire>[^"]*)",\s*"(?<schema>[^"]*)"\)""",
        RegexOptions.CultureInvariant);

    private static string UseCurrentIdentitySdk(string source)
    {
        source = source.Replace(
            "public RpcCodecAdapterRegistrationAttribute(Type adapterType, string adapterId, string wireFormatId) { }",
            "public RpcCodecAdapterRegistrationAttribute(Type adapterType, string adapterId) { }",
            StringComparison.Ordinal);
        source = source.Replace(
            """
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class RpcCodecImplementationAttribute : Attribute
    {
        public RpcCodecImplementationAttribute(string wireFormatId, string schemaId) { }
    }
""",
            """
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    public sealed class RpcCodecSemanticIdentityAttribute : Attribute
    {
        public RpcCodecSemanticIdentityAttribute(ulong high, ulong low) { }
    }
""",
            StringComparison.Ordinal);

        var registrations = LegacyAdapterRegistrationPattern.Matches(source)
            .Cast<Match>()
            .Select(static match => new LegacyAdapterRegistration(
                match.Groups["type"].Value.Trim(),
                match.Groups["id"].Value,
                match.Groups["wire"].Value))
            .ToArray();
        source = LegacyAdapterRegistrationPattern.Replace(source, static match =>
            $"RpcCodecAdapterRegistration(typeof({match.Groups["type"].Value}), " +
            $"\"{match.Groups["id"].Value}\"{match.Groups["tail"].Value})");

        source = LegacyCodecIdentityPattern.Replace(source, static match =>
        {
            var identity = GetFixtureSemanticIdentity(match.Groups["wire"].Value, match.Groups["schema"].Value);
            return $"{match.Groups["prefix"].Value}RpcCodecSemanticIdentity({FormatHash(identity.High)}UL, {FormatHash(identity.Low)}UL)";
        });

        foreach (var registration in registrations)
        {
            var identity = GetFixtureSemanticIdentity(registration.AdapterId, registration.WireFormatId);
            source = AddSemanticIdentityToRegisteredAdapter(source, registration.AdapterType, identity);
        }

        return source;
    }

    private static string AddSemanticIdentityToRegisteredAdapter(
        string source,
        string adapterType,
        (ulong High, ulong Low) identity)
    {
        var simpleName = adapterType.Split('.').Last().Trim();
        var typePattern = new Regex(
            $"(?m)^(?<indent>\\s*)(?<declaration>(?:public|internal|protected|private)\\s+(?:(?:static|abstract|sealed|partial)\\s+)*class\\s+{Regex.Escape(simpleName)}\\b)",
            RegexOptions.CultureInvariant);
        var match = typePattern.Match(source);
        if (!match.Success)
            return source;

        var previousBlockStart = source.LastIndexOf("\n\n", Math.Max(0, match.Index - 1), StringComparison.Ordinal);
        var previousBlockLength = match.Index - (previousBlockStart < 0 ? 0 : previousBlockStart + 2);
        var previousBlock = source.Substring(previousBlockStart < 0 ? 0 : previousBlockStart + 2, previousBlockLength);
        if (previousBlock.Contains("RpcCodecSemanticIdentity", StringComparison.Ordinal))
            return source;

        var indentation = match.Groups["indent"].Value;
        var attribute =
            $"{indentation}[SharpLink.Sdk.RpcCodecSemanticIdentity({FormatHash(identity.High)}UL, {FormatHash(identity.Low)}UL)]\n";
        return source.Insert(match.Index, attribute);
    }

    private static (ulong High, ulong Low) GetFixtureSemanticIdentity(string first, string second)
    {
        const ulong fnvPrime = 1099511628211UL;
        ulong high = 14695981039346656037UL;
        ulong low = 7809847782465536322UL;
        foreach (var value in EnumerateIdentityChars(first, second))
        {
            unchecked
            {
                high = (high ^ value) * fnvPrime;
                low = (low ^ (value + 0x9e37UL)) * 14029467366897019727UL;
            }
        }

        if ((high | low) == 0)
            low = 1;
        return (high, low);
    }

    private static IEnumerable<ulong> EnumerateIdentityChars(string first, string second)
    {
        foreach (var value in first)
            yield return value;
        yield return 0;
        foreach (var value in second)
            yield return value;
    }

    private static string FormatHash(ulong value)
        => "0x" + value.ToString("x16", CultureInfo.InvariantCulture);

    private static ImmutableArray<Diagnostic> RunGenerator(string source)
        => RunGenerator(UseCurrentIdentitySdk(source), Array.Empty<MetadataReference>());

    private static ImmutableArray<Diagnostic> RunGenerator(string source, MetadataReference first)
        => RunGenerator(UseCurrentIdentitySdk(source), [first]);

    private static ImmutableArray<Diagnostic> RunGenerator(
        string source,
        MetadataReference first,
        MetadataReference second)
        => RunGenerator(UseCurrentIdentitySdk(source), [first, second]);

    private static ImmutableArray<Diagnostic> RunGenerator(
        string source,
        MetadataReference first,
        MetadataReference second,
        MetadataReference third)
        => RunGenerator(UseCurrentIdentitySdk(source), [first, second, third]);

    private static string[] RunGeneratorAndGetSources(string source)
        => RunGeneratorAndGetSources(UseCurrentIdentitySdk(source), Array.Empty<MetadataReference>());

    private static string[] RunGeneratorAndGetSources(string source, MetadataReference first)
        => RunGeneratorAndGetSources(UseCurrentIdentitySdk(source), [first]);

    private static string[] RunGeneratorAndGetSources(
        string source,
        MetadataReference first,
        MetadataReference second)
        => RunGeneratorAndGetSources(UseCurrentIdentitySdk(source), [first, second]);

    private static string[] RunGeneratorAndGetSources(
        string source,
        MetadataReference first,
        MetadataReference second,
        MetadataReference third)
        => RunGeneratorAndGetSources(UseCurrentIdentitySdk(source), [first, second, third]);

    private static MetadataReference CreateMetadataReference(string assemblyName, string source)
        => CreateMetadataReference(assemblyName, UseCurrentIdentitySdk(source), Array.Empty<MetadataReference>());

    private static MetadataReference CreateMetadataReference(
        string assemblyName,
        string source,
        MetadataReference first)
        => CreateMetadataReference(assemblyName, UseCurrentIdentitySdk(source), [first]);

    private static MetadataReference CreateMetadataReference(
        string assemblyName,
        string source,
        MetadataReference first,
        MetadataReference second)
        => CreateMetadataReference(assemblyName, UseCurrentIdentitySdk(source), [first, second]);

    private static MetadataReference CreateMetadataReference(
        string assemblyName,
        string source,
        MetadataReference first,
        MetadataReference second,
        MetadataReference third)
        => CreateMetadataReference(assemblyName, UseCurrentIdentitySdk(source), [first, second, third]);

    private readonly record struct LegacyAdapterRegistration(
        string AdapterType,
        string AdapterId,
        string WireFormatId);
}
