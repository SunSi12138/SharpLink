#!/usr/bin/env python3
"""Create identical NativeAOT-capable copies of the existing real-RPC timing harness."""
import hashlib
import json
import pathlib
import subprocess
import sys

if len(sys.argv) != 4:
    raise SystemExit('usage: prepare-native-rpc.py CONTROL_ROOT CANDIDATE_ROOT EVIDENCE_OUTPUT')
control, candidate, evidence = map(lambda x:pathlib.Path(x).resolve(),sys.argv[1:])
CONTROL = 'e834d3c28c87ad496989af925515cf21babd308d'
FILES = ('AllocationReadProbe.cs','BenchmarkContracts.cs','BenchmarkEnvironment.cs',
         'BenchmarkService.cs','GeneratedAbiStreamingEvidenceRunner.cs',
         'ServerLifecycleTestExtensions.cs')

project = '''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>SharpLink.Benchmarks</AssemblyName>
    <RootNamespace>SharpLink.Benchmarks</RootNamespace>
    <PublishAot>true</PublishAot>
    <IsPackable>false</IsPackable>
    <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/SharpLink.Abstractions/SharpLink.Abstractions.csproj" />
    <ProjectReference Include="../../src/SharpLink.Runtime/SharpLink.Runtime.csproj" />
    <ProjectReference Include="../../src/SharpLink.Client/SharpLink.Client.csproj" />
    <ProjectReference Include="../../src/SharpLink.Server/SharpLink.Server.csproj" />
    <ProjectReference Include="../../src/SharpLink.Compression.Zstd/SharpLink.Compression.Zstd.csproj" />
    <ProjectReference Include="../../src/SharpLink.Sdk/SharpLink.Sdk.csproj" />
    <ProjectReference Include="../../src/SharpLink.Serializer.SharpPack/SharpLink.Serializer.SharpPack.csproj" />
    <ProjectReference Include="../../src/SharpLink.Generator/SharpLink.Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" GlobalPropertiesToRemove="RuntimeIdentifier;SelfContained;PublishAot;PublishSingleFile" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="SharpPack" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Logging" />
  </ItemGroup>
</Project>
'''
context = '''using System.Text.Json.Serialization;
namespace SharpLink.Benchmarks;
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(GeneratedAbiStreamingEvidenceResult))]
internal partial class NativeRpcPrettyJsonContext : JsonSerializerContext { }
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GeneratedAbiStreamingEvidenceResult))]
internal partial class NativeRpcCompactJsonContext : JsonSerializerContext { }
'''
program = '''using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
namespace SharpLink.Benchmarks;
internal static class NativeRpcProgram
{
    public static async Task Main(string[] args)
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
            throw new InvalidOperationException("This evidence host must execute as actual NativeAOT.");
        Console.WriteLine("NATIVE_RPC_EVIDENCE dynamicCodeSupported=False");
        await GeneratedAbiStreamingEvidenceRunner.RunAsync(args).ConfigureAwait(false);
    }
}
'''

def replace(text,old,new,count):
    if text.count(old)!=count:
        raise RuntimeError(f'Unexpected serialization anchor count for {old!r}: {text.count(old)}')
    return text.replace(old,new)

source={}
original={}
for name in FILES:
    relative=pathlib.Path('test/SharpLink.Benchmarks')/name
    left=(control/relative).read_bytes()
    right=(candidate/relative).read_bytes()
    if left!=right:
        raise RuntimeError(f'Runtime arms disagree on common harness {name}')
    expected=subprocess.check_output(['git','show',f'{CONTROL}:{relative.as_posix()}'],cwd=control)
    if left!=expected:
        raise RuntimeError(f'Measurement harness differs from pinned control: {name}')
    original[name]=hashlib.sha256(left).hexdigest()
    text=left.decode()
    if name=='GeneratedAbiStreamingEvidenceRunner.cs':
        start=text.index('        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);')
        finish=text.index('        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);',start)
        timed=text[start:finish]
        text=replace(text,'JsonSerializer.Serialize(resultDocument, SJsonOptions)',
            'JsonSerializer.Serialize(resultDocument, NativeRpcPrettyJsonContext.Default.GeneratedAbiStreamingEvidenceResult)',2)
        text=replace(text,'JsonSerializer.DeserializeAsync<GeneratedAbiStreamingEvidenceResult>(\n                stream,\n                SJsonOptions)',
            'JsonSerializer.DeserializeAsync(\n                stream,\n                NativeRpcPrettyJsonContext.Default.GeneratedAbiStreamingEvidenceResult)',1)
        text=replace(text,'JsonSerializer.Serialize(item, SJsonLinesOptions)',
            'JsonSerializer.Serialize(item, NativeRpcCompactJsonContext.Default.GeneratedAbiStreamingEvidenceResult)',1)
        if timed not in text:
            raise RuntimeError('The measured loop or result accounting was changed')
        timed_hash=hashlib.sha256(timed.encode()).hexdigest()
    source[name]=text
source.update({'SharpLink.FirstReceiveRpcEvidence.csproj':project,
               'NativeRpcJsonContext.cs':context,'Program.cs':program})
for root in (control,candidate):
    output=root/'test/SharpLink.FirstReceiveRpcEvidence'
    output.mkdir(parents=True,exist_ok=True)
    for name,text in source.items():
        (output/name).write_text(text)
    if set(p.name for p in output.iterdir())!=set(source):
        raise RuntimeError(f'Unexpected files in minimal evidence host: {output}')
provenance=dict(control=CONTROL,original_harness_sha256=original,
    common_host_sha256={name:hashlib.sha256(text.encode()).hexdigest() for name,text in source.items()},
    measured_loop_sha256=timed_hash,
    adaptation='only out-of-measurement JSON uses generated metadata; service, contracts, transport environment, lifecycle helper and measured loop retained',
    comparison='two actual NativeAOT runtime revisions with one identical minimal generated RPC host')
evidence.mkdir(parents=True,exist_ok=True)
(evidence/'native-host-provenance.json').write_text(json.dumps(provenance,indent=2)+'\n')
for name,text in source.items():
    target=evidence/'native-host'/name
    target.parent.mkdir(parents=True,exist_ok=True)
    target.write_text(text)
print(json.dumps(provenance,indent=2))
