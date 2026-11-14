#!/usr/bin/env python3
"""Compile external API6 SPI controls against the actual candidate package.

Migrates #973's package-only resolver control to TryResolveMethod/in descriptor.
This is source compatibility evidence, not a previous-generated-binary loader test.
"""

import argparse
from collections import Counter
import json
import os
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
RESOLVER = "public bool TryResolveMethod(long id, out RpcMethodDescriptor method) { method = default; return false; }"
PREPARE = "public ValueTask PrepareAsync(TimeSpan timeout, CancellationToken token = default) => default;"
SOURCE = """using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Sdk;
public interface IExternalContract : IService { }
public sealed class ExternalStub : IRpcStub
{
    RESOLVER
    public long InterfaceHash => 1;
    public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge,
        in RpcMethodDescriptor method, long id, ReadOnlySequence<byte> args) => default;
    public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge,
        in RpcMethodDescriptor method, long id, ReadOnlySequence<byte> args, CancellationToken token) => default;
    public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge,
        in RpcMethodDescriptor method, long id, ReadOnlySequence<byte> args, IBufferWriter<byte> output) => default;
    public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge,
        in RpcMethodDescriptor method, long id, ReadOnlySequence<byte> args, IBufferWriter<byte> output,
        CancellationToken token) => default;
}
public sealed class ExternalChannel : IRpcContractChannel
{
    PREPARE
    public IRpcRuntimeContext RuntimeContext => throw new NotSupportedException();
    public ValueTask<TResponse> InvokeUnaryAsync<TRequest, TResponse>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        SharpLinkMetadata? metadata, CancellationToken token = default) => default;
    public ValueTask InvokeOneWayAsync<TRequest, TStreams>(RpcMethodDescriptor method, in TRequest request,
        IRpcCodec<TRequest> requestCodec, in TStreams streams, SharpLinkMetadata? metadata,
        CancellationToken token = default) where TStreams : struct, IRpcClientStreamWriter => default;
    public ValueTask<TResponse> InvokeClientStreamingAsync<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        in TStreams streams, SharpLinkMetadata? metadata, CancellationToken token = default)
        where TStreams : struct, IRpcClientStreamWriter => default;
    public IAsyncEnumerable<TResponse> InvokeServerStreamingAsync<TRequest, TResponse>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        SharpLinkMetadata? metadata, CancellationToken token = default) => throw new NotSupportedException();
    public IAsyncEnumerable<TResponse> InvokeDuplexStreamingAsync<TRequest, TResponse, TStreams>(RpcMethodDescriptor method,
        in TRequest request, IRpcCodec<TRequest> requestCodec, IRpcCodec<TResponse> responseCodec,
        in TStreams streams, SharpLinkMetadata? metadata, CancellationToken token = default)
        where TStreams : struct, IRpcClientStreamWriter => throw new NotSupportedException();
}
public static class Consumer
{
    public static RpcProxy<IExternalContract> Create(ISharpLinkClient client) => client.CreateProxy<IExternalContract>(new()
        { Metadata = null, Receipt = null, Requirement = RpcRegistrationRequirement.Compatible });
    public static IRpcContractChannel Channel(ISharpLinkClient client) => client.CreateContractChannel(client.GetLocalContract<IExternalContract>());
}
""".replace("RESOLVER", RESOLVER).replace("PREPARE", PREPARE)


def cases():
    yield "positive", SOURCE, []
    yield "missing-resolver", SOURCE.replace(RESOLVER, ""), [("CS0535", "IRpcStub.TryResolveMethod(")]
    yield "by-value-facts", SOURCE.replace("in RpcMethodDescriptor method", "RpcMethodDescriptor method"), [
        ("CS0535", f"IRpcStub.{name}(") for name in (
            "InvokeNoReturnAsync", "InvokeNoReturnCancellableAsync", "InvokeAsync", "InvokeCancellableAsync")]
    yield "missing-prepare", SOURCE.replace(PREPARE, ""), [("CS0535", "IRpcContractChannel.PrepareAsync(")]
    yield "removed-raw-channel", SOURCE + "public sealed class Raw { public IRpcChannel Channel = default!; }", [("CS0246", "IRpcChannel")]
    yield "removed-get", SOURCE + "public static class Old { public static object Get(ISharpLinkClient c) => c.Get<IExternalContract>(); }", [("CS1061", "Get")]
    yield "opaque-local", SOURCE + "public static class Forge { public static object Make() => new RpcLocalContract(); }", [("CS1729", "RpcLocalContract")]
    yield "opaque-proxy", SOURCE + "public static class Forge { public static object Make() => new RpcProxy<IExternalContract>(null!, null!); }", [("CS1729", "RpcProxy<IExternalContract>")]
    yield "sealed-options", SOURCE + "public sealed class Derived : RpcProxyOptions { }", [("CS0509", "RpcProxyOptions")]
    for kind in ("Registration", "Replacement"):
        name = f"SharpLinkServerAssembly{kind}Result"
        yield f"opaque-{kind.lower()}-result", SOURCE + f"public static class Forge {{ public static object Make() => new {name}(); }}", [("CS1729", name)]


def validate_diagnostics(errors, expected):
    """Reject unrelated/missing/duplicate diagnostics, not merely any build failure."""
    remaining = list(expected)
    for error in errors:
        message = error.get("message", {})
        text = message.get("text", "") if isinstance(message, dict) else message
        match = next((item for item in remaining if error.get("ruleId") == item[0] and item[1] in text), None)
        if match is None:
            raise ValueError(f"Unexpected compiler diagnostic: {error}")
        remaining.remove(match)
    if remaining:
        raise ValueError(f"Missing expected diagnostics: {remaining}")


def run(command, log, environment):
    result = subprocess.run(command, cwd=ROOT, env=environment, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, encoding="utf-8", errors="replace")
    log.write_text(result.stdout, encoding="utf-8")
    return result.returncode


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/packaged-method-facts")
    options = parser.parse_args()
    version = ET.parse(ROOT / "Directory.Build.props").findtext(".//VersionPrefix")
    feed = ROOT / "artifacts/nuget"
    package = feed / f"SharpLink.Abstractions.{version}.nupkg"
    with zipfile.ZipFile(package) as archive:
        nuspec = ET.fromstring(archive.read("SharpLink.Abstractions.nuspec"))
        metadata = next(item for item in nuspec if item.tag.endswith("metadata"))
        repository = next(item for item in metadata if item.tag.endswith("repository"))
        commit = repository.attrib.get("commit")
        if not commit:
            raise ValueError("Candidate package must record its repository commit.")
    options.output.mkdir(parents=True, exist_ok=True)
    # Keep every attempt and compiler log; never delete an earlier failing attempt.
    output = Path(tempfile.mkdtemp(prefix="attempt-", dir=options.output.resolve()))
    environment = os.environ.copy()
    environment.update(NUGET_PACKAGES=str(output / "packages"),
                       NUGET_HTTP_CACHE_PATH=str(output / "http"), DOTNET_CLI_UI_LANGUAGE="en-US")
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="candidate", value=str(feed))
    config_path = output / "NuGet.config"
    ET.ElementTree(config).write(config_path, encoding="utf-8", xml_declaration=True)
    outcomes = []
    for name, source, expected in cases():
        directory = output / name
        directory.mkdir()
        project = directory / "ExternalConsumer.csproj"
        project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable>
    <AssemblyName>ExternalBindingConsumer</AssemblyName><TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup><PackageReference Include="SharpLink.Abstractions" Version="[{version}]" /></ItemGroup>
</Project>
''', encoding="utf-8")
        (directory / "Consumer.cs").write_text(source, encoding="utf-8")
        common = [str(project), "-p:ImportDirectoryBuildProps=false", "-p:ImportDirectoryBuildTargets=false"]
        if run(["dotnet", "restore", *common, "--configfile", str(config_path), "--no-cache"], directory / "restore.log", environment):
            raise RuntimeError(f"{name}: restore failed; no negative result accepted. See {directory}")
        assets = json.loads((directory / "obj/project.assets.json").read_text(encoding="utf-8"))
        if assets.get("libraries", {}).get(f"SharpLink.Abstractions/{version}", {}).get("type") != "package":
            raise ValueError("External consumer did not restore the exact candidate package.")
        sarif = directory / "diagnostics.sarif"
        code = run(["dotnet", "build", *common, "--no-restore", "-c", "Release", "-m:1",
                    "-p:UseSharedCompilation=false", "-nodeReuse:false", f"-p:ErrorLog={sarif}"], directory / "build.log", environment)
        data = json.loads(sarif.read_text(encoding="utf-8-sig"))
        errors = [error for run_data in data["runs"] for error in run_data.get("results", []) if error.get("level") == "error"]
        assembly = directory / "bin/Release/net10.0/ExternalBindingConsumer.dll"
        validate_diagnostics(errors, expected)
        if (code != 0) != bool(expected) or assembly.exists() != (not expected):
            raise ValueError(f"{name}: wrong exit/output assembly state. See {directory}")
        outcomes.append(dict(name=name, exitCode=code, assemblyProduced=assembly.exists(),
                             diagnostics=dict(Counter(error["ruleId"] for error in errors))))
        print(f"{name}: PASS", flush=True)
    (output / "result.json").write_text(json.dumps(dict(package=package.name, sourceCommit=commit,
        scope="actual-package source compatibility; not historical binary loading", cases=outcomes), indent=2) + "\n", encoding="utf-8")
    print(f"Packaged binding SPI gate passed; evidence: {output}")


if __name__ == "__main__":
    main()
