#!/usr/bin/env python3
"""Require ResolveMethodFacts on an external consumer of the actual packed DLL.

Run after packing. This is a source-compatibility negative, not a retained-binary
loader test or evidence about older API5 generated assemblies.
"""
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
VERSION = ET.parse(ROOT / "Directory.Build.props").findtext(".//VersionPrefix")
FEED = ROOT / "artifacts/nuget"
PACKAGE = FEED / f"SharpLink.Abstractions.{VERSION}.nupkg"
RESULTS = ROOT / "artifacts/packaged-method-facts"
SOURCE = """using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
public sealed class ExternalStub : IRpcStub
{
    RESOLVER
    public long InterfaceHash => 1;
    public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge,
        RpcMethodFacts method, long requestId, ReadOnlySequence<byte> args) => default;
    public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge,
        RpcMethodFacts method, long requestId, ReadOnlySequence<byte> args, CancellationToken token) => default;
    public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge,
        RpcMethodFacts method, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output) => default;
    public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge,
        RpcMethodFacts method, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output,
        CancellationToken token) => default;
}
"""
RESOLVER = "public RpcMethodFacts ResolveMethodFacts(long id) => RpcMethodFacts.UnknownMethod;"


def run(arguments, log):
    result = subprocess.run(arguments, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    log.write_text(result.stdout)
    return result.returncode


def main():
    RESULTS.mkdir(parents=True, exist_ok=True)
    (RESULTS / "result.json").unlink(missing_ok=True)
    if not PACKAGE.is_file():
        raise SystemExit(f"Pack {PACKAGE.name} into artifacts/nuget first.")
    # Fresh projects AND cache: a same-version cached package must not stand in
    # for the candidate. A local-only feed makes restore failures inconclusive.
    with tempfile.TemporaryDirectory(prefix="method-facts-", dir=RESULTS) as temporary:
        work = Path(temporary)
        config = ET.Element("configuration")
        sources = ET.SubElement(config, "packageSources")
        ET.SubElement(sources, "clear")
        ET.SubElement(sources, "add", key="candidate", value=str(FEED))
        config_path = work / "NuGet.config"
        ET.ElementTree(config).write(config_path, encoding="utf-8", xml_declaration=True)
        for name, resolver in [("positive", RESOLVER), ("missing-resolver", "")]:
            directory = work / name
            directory.mkdir()
            project = directory / "ExternalConsumer.csproj"
            project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>ExternalMethodFactsConsumer</AssemblyName>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SharpLink.Abstractions" Version="[{VERSION}]" />
  </ItemGroup>
</Project>
''')
            (directory / "ExternalStub.cs").write_text(SOURCE.replace("RESOLVER", resolver))
            common = [str(project), "-p:ImportDirectoryBuildProps=false", "-p:ImportDirectoryBuildTargets=false"]
            if run(["dotnet", "restore", *common, "--configfile", str(config_path),
                    "--packages", str(work / "packages"), "--no-cache"], RESULTS / f"{name}.restore.log"):
                raise SystemExit(f"{name}: restore failed; no negative result accepted.")
            sarif = directory / "diagnostics.sarif"
            code = run(["dotnet", "build", *common, "--no-restore", "-c", "Release", "-m:1",
                        "-p:UseSharedCompilation=false", "-nodeReuse:false", f"-p:ErrorLog={sarif}"],
                       RESULTS / f"{name}.build.log")
            assembly = directory / "bin/Release/net10.0/ExternalMethodFactsConsumer.dll"
            if name == "positive":
                if code or not assembly.is_file():
                    raise SystemExit("Positive control failed; no negative result accepted.")
            else:
                if not code or assembly.exists() or not sarif.is_file():
                    raise SystemExit("Negative must fail compilation without producing an assembly.")
                data = json.loads(sarif.read_text())
                errors = [item for run_data in data["runs"] for item in run_data.get("results", [])
                          if item.get("level") == "error"]
                if len(errors) != 1 or errors[0]["ruleId"] != "CS0535" or \
                        "IRpcStub.ResolveMethodFacts(long)" not in str(errors[0]["message"]):
                    raise SystemExit(f"Unexpected compiler diagnostics: {errors}")
                (RESULTS / "missing-resolver.sarif").write_text(sarif.read_text())
    (RESULTS / "result.json").write_text(json.dumps({
        "package": PACKAGE.name, "sha256": hashlib.sha256(PACKAGE.read_bytes()).hexdigest(),
        "positive": "compiled", "negative": "CS0535: IRpcStub.ResolveMethodFacts(long)",
        "scope": "actual-package source compatibility; not previous-API5 binary loading",
    }, indent=2) + "\n")
    print("Packaged method-facts gate passed: resolver required by the shipped assembly.")


if __name__ == "__main__":
    main()
