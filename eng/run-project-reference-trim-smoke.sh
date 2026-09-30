#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="${SHARPLINK_TRIM_RID:-}"
OUTPUT="${SHARPLINK_TRIM_OUTPUT:-$ROOT/artifacts/project-reference-trim-smoke}"
WORK="$OUTPUT/source"
SDK_ARTIFACTS="$OUTPUT/sdk-artifacts"
PUBLISH="$OUTPUT/publish"

if [[ -z "$RID" ]]; then
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) RID=linux-x64 ;;
    Darwin-arm64) RID=osx-arm64 ;;
    MINGW*|MSYS*|CYGWIN*) RID=win-x64 ;;
    *) echo "Unsupported trim smoke host: $(uname -s)-$(uname -m)" >&2; exit 2 ;;
  esac
fi

GENERATOR="$ROOT/src/SharpLink.Generator/bin/Release/netstandard2.0/SharpLink.Generator.dll"
if [[ ! -f "$GENERATOR" ]]; then
  dotnet build "$ROOT/src/SharpLink.Generator/SharpLink.Generator.csproj" \
    -c Release \
    /p:PublishTrimmed=false \
    /p:TrimMode=partial \
    -v minimal
fi

rm -rf "$OUTPUT"
mkdir -p "$WORK" "$PUBLISH"

cat > "$WORK/ProjectReferenceTrimSmoke.csproj" <<'XML'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <PublishTrimmed>true</PublishTrimmed>
    <PublishAot>false</PublishAot>
    <TrimMode>full</TrimMode>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnablePackageValidation>false</EnablePackageValidation>
    <PackageValidationBaselineVersion></PackageValidationBaselineVersion>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="../../../src/SharpLink.Abstractions/SharpLink.Abstractions.csproj" />
    <ProjectReference Include="../../../src/SharpLink.Runtime/SharpLink.Runtime.csproj" />
    <Analyzer Include="../../../src/SharpLink.Generator/bin/Release/netstandard2.0/SharpLink.Generator.dll" />
  </ItemGroup>
</Project>
XML

cat > "$WORK/Program.cs" <<'CS'
using System.Buffers;
using System.Runtime.InteropServices;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.Sdk;

[assembly: SharpLinkClusterContractAssembly(
    "project-reference-trim",
    typeof(ProjectReferenceTrimSmoke.IProjectReferenceTrimContract))]

namespace ProjectReferenceTrimSmoke;

[StructLayout(LayoutKind.Sequential)]
public struct GeneratedTrimPayload
{
    public int Count;
    public long Stamp;
}

[RpcContract]
public interface IProjectReferenceTrimContract : IService
{
    [NonCancellable]
    ValueTask<GeneratedTrimPayload> EchoAsync(GeneratedTrimPayload value);
}

public static class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct UnregisteredTrimPayload
    {
        public int Value;
    }

    public static void Main()
    {
        if (!SharpLinkGeneratedUnsafeBlitCatalog.TryGet(
                typeof(GeneratedTrimPayload),
                out var requirement))
        {
            throw new InvalidOperationException(
                "ProjectReference trim smoke did not receive generated UnsafeBlit ABI metadata.");
        }

        if (requirement.NativePointerWidth != IntPtr.Size)
        {
            throw new InvalidOperationException(
                $"Generated UnsafeBlit ABI pointer width {requirement.NativePointerWidth} " +
                $"does not match runtime width {IntPtr.Size}.");
        }

        using var context = new SharpLinkRuntimeContextBuilder().Build();
        var codec = context.Codecs.GetCodec<GeneratedTrimPayload>();

        var expected = new GeneratedTrimPayload
        {
            Count = 42,
            Stamp = 0x0102030405060708
        };

        var writer = new ArrayBufferWriter<byte>();
        codec.Serialize(in expected, writer);
        var sequence = new ReadOnlySequence<byte>(writer.WrittenMemory);
        var actual = codec.Deserialize(in sequence);
        if (actual.Count != expected.Count || actual.Stamp != expected.Stamp)
        {
            throw new InvalidOperationException(
                "ProjectReference trimmed UnsafeBlit round-trip failed.");
        }

        try
        {
            _ = context.Codecs.GetCodec<UnregisteredTrimPayload>();
            throw new InvalidOperationException(
                "ProjectReference trimmed consumer accepted UnsafeBlit without generated ABI metadata.");
        }
        catch (PlatformNotSupportedException exception)
            when (exception.Message.Contains("source-generated ABI metadata", StringComparison.Ordinal))
        {
        }

        Console.WriteLine("PROJECT_REFERENCE_TRIM_SMOKE_PASS");
    }
}
CS

dotnet publish "$WORK/ProjectReferenceTrimSmoke.csproj" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  --artifacts-path "$SDK_ARTIFACTS" \
  -o "$PUBLISH" \
  /p:PublishAot=false \
  /p:PublishTrimmed=true \
  /p:TrimMode=full \
  /p:TreatWarningsAsErrors=true \
  /p:TrimmerSingleWarn=false \
  -v minimal

EXE="$PUBLISH/ProjectReferenceTrimSmoke"
if [[ "$RID" == win-* ]]; then
  EXE="$EXE.exe"
fi

"$EXE" | tee "$OUTPUT/run.log"
grep -q "PROJECT_REFERENCE_TRIM_SMOKE_PASS" "$OUTPUT/run.log"
