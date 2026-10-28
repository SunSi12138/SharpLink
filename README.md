# SharpLink 3.0 persistent release evidence

This dedicated evidence branch preserves raw task artifacts beyond Actions retention. It is **not a release, not a Go decision, and not the product source branch**. Follow the current root handoff on the product/documentation branch for decisions.

```bash
set -euo pipefail
git clone --single-branch --branch codex/3-release-evidence https://github.com/SunSi12138/SharpLink.git SharpLink-release-evidence
cd SharpLink-release-evidence
# A fresh directory prevents overwriting tracked archives or previous extractions.
verified_dir=$(mktemp -d "$PWD/verified-evidence.XXXXXX")
# Verify ALL indexed archives, including BOTH split R1 and phase archives.
# Explicit failure handling prevents any extraction after any verification failure.
if ! python3 - "$verified_dir" <<'PY_REASSEMBLE'
import hashlib, json, sys
from pathlib import Path
root = Path.cwd().resolve()
out = Path(sys.argv[1]).resolve()
def source(name):
    path = (root / name).resolve(strict=True)
    if not path.is_relative_to(root) or not path.is_file():
        raise SystemExit('Invalid input path: ' + name)
    return path
def check(data, size, digest, label):
    if len(data) != size or hashlib.sha256(data).hexdigest() != digest:
        raise SystemExit('Integrity failure: ' + label)
entries = json.loads(source('index.json').read_text())['archives']
if not entries:
    raise SystemExit('Empty archive index')
(out / 'archives').mkdir()
for entry in entries:
    name = Path(entry['archive']).name
    if entry['archive'] != 'archives/' + name or not name.endswith('.tar.gz'):
        raise SystemExit('Invalid archive path')
    if entry.get('storage') == 'ordered-binary-parts':
        manifest = json.loads(source(entry['partsManifest']).read_text())
        if (manifest['reassembledArchive'] != entry['archive'] or
            manifest['archiveBytes'] != entry['archiveBytes'] or
            manifest['archiveSha256'] != entry['sha256'] or
            len(manifest['parts']) != entry['partCount']):
            raise SystemExit('Manifest/index mismatch: ' + name)
        chunks, seen = [], set()
        for order, part in enumerate(manifest['parts'], 1):
            if part['order'] != order or part['path'] in seen:
                raise SystemExit('Invalid/repeated/reordered part: ' + part['path'])
            seen.add(part['path'])
            chunk = source(part['path']).read_bytes()
            check(chunk, part['bytes'], part['sha256'], part['path'])
            chunks.append(chunk)
        data = b''.join(chunks)
    elif entry.get('storage') is None:
        data = source(entry['archive']).read_bytes()
    else:
        raise SystemExit('Unsupported storage: ' + name)
    check(data, entry['archiveBytes'], entry['sha256'], name)
    with (out / 'archives' / name).open('xb') as target:
        target.write(data)
    print('Verified:', name, len(data), entry['sha256'])
PY_REASSEMBLE
then
    echo "Verification failed; no extraction performed. Partial output retained: $verified_dir" >&2
    exit 1
fi
# Only reached after EVERY indexed archive has passed verification.
# Each archive gets its own fresh extraction directory, avoiding cross-archive collisions.
mkdir "$verified_dir/extracted"
for archive in "$verified_dir"/archives/*.tar.gz; do
    name=$(basename "$archive" .tar.gz)
    mkdir "$verified_dir/extracted/$name"
    tar -xzf "$archive" -C "$verified_dir/extracted/$name"
done
printf 'Verified archives and separate extractions: %s\n' "$verified_dir"
```

The machine inventory is [index.json](index.json). It currently contains **14 logical archives, 229,505,339 bytes total**; all prior thirteen archive entries and stored bytes remain unchanged. R1 full-product and phase-rewrite archives are stored as 11 and 30 ordered binary parts respectively; their logical `.tar.gz` paths are reconstructed under the fresh verification directory above, never over tracked originals. Totals count each logical compressed archive once, not metadata or duplicate monolithic files. The phase archive is **88.78 MiB** (93,087,850 bytes), so logical archives are not all below 50 MiB. Each stored binary part is at most **3 MiB**; unsplit tracked archives remain below 50 MiB. Each archive has a file inventory and transfer-integrity identity. Source commit/arm/binary identities inside the original evidence remain authoritative; this mirror commit is not their source SHA.

| Archive | MiB | Files | Status |
|---|---:|---:|---|
| [931-original-48](archives/931-original-48.tar.gz) | 12.57 | 457 | NoGo: original 48-process paired evidence; adverse dynamic100 results retained |
| [931-fullopts-12](archives/931-fullopts-12.tar.gz) | 12.90 | 291 | Mechanism ablation only; not product acceptance |
| [732-hosted-three-arm-36](archives/732-hosted-three-arm-36.tar.gz) | 21.03 | 463 | Three-arm evidence; independent source-bound decision required, not a release Go |
| [931-pgo-12](archives/931-pgo-12.tar.gz) | 13.29 | 291 | PGO ablation only; not product acceptance |
| [732-m0-original](archives/732-m0-original.tar.gz) | 11.45 | 152 | M0 NoGo; original adverse result retained |
| [732-m1-local](archives/732-m1-local.tar.gz) | 5.41 | 145 | Inconclusive local comparison: external CPU/load contamination; never relabel as clean evidence |
| [931-r1-loop-exit](archives/931-r1-loop-exit.tar.gz) | 6.18 | 114 | R1 local mechanism/correctness experiment; no end-to-end performance Go |
| [933-repair-40636189267](archives/933-repair-40636189267.tar.gz) | 8.01 | 4209 | Repair/source preparation evidence after failed canonical run; not final release acceptance |
| [workflow-logs](archives/workflow-logs.tar.gz) | 0.04 | 8 | Unmodified hosted run logs; failures retained |
| [release-status-logs](archives/release-status-logs.tar.gz) | 0.06 | 14 | Historical status and diagnostic logs; current root handoff and precise candidate gates prevail |
| [r1-cloud-native-20261028](archives/r1-cloud-native-20261028.tar.gz) | 3.17 | 97 | Failed four-target Tier1 gate in both cloud arms; preparation failures retained; no timing or acceptance |
| [r1-hosted-native-40681832353](archives/r1-hosted-native-40681832353.tar.gz) | 3.21 | 98 | Valid non-timed mechanism and8focused tests only; not performance Go; cloud failures retained |
| [r1-full-product-40687272444](archives/r1-full-product-40687272444.parts/manifest.json) | 32.76 | 611 | Valid48+14diagnostic; adverse dynamic1tails, mixed other cells; NoGo unchanged.11lossless parts |
| [phase-rewrite-performance-20261028](archives/phase-rewrite-performance-20261028.parts/manifest.json) | 88.78 | 5072 | Separate local and hosted 48 TCP + 14 scalar diagnostics; no demonstrated throughput/CPU gain, descriptive tail risk; candidate not promoted. 30 lossless parts |

The new R1 cloud archive contains attempts001/002/003. Executed native tool is
`17038d5014d1d4a76407fa13c451d6a0606968ff`; product R1 remains `94adeae8`.
The embedded report/workflow candidate `5c6606a` was **not** the executed tool.
The later [Draft validation PR956](https://github.com/SunSi12138/SharpLink/pull/956)
uses a separate hosted environment and is not evidence inside this cloud archive.
SHA256 is `d38f97ee440ddd639e15a9e47774c8981584a374c85ccbecaa9adcbbc03c36cf`.
Its [file/provenance/privacy inventory](r1-cloud-native-20261028-files.json)
records all97 member hashes, zero credential-pattern findings and explicit cache
exclusions; independent review checked the exact archive bytes. Raw proof, host,
native, DLL and failure logs are unmodified. Pattern scanning is not an absolute
absence guarantee. Archive runtime metadata is under `runtime-provenance/`; the
frozen plan is embedded in `reviewed-tool-candidate.patch`, not a loose file.

## Privacy and exclusions

Local originals are unchanged. All measurement timing ticks and `*.samples.jsonl` are copied without transformation. M1 was contaminated by other host work: numeric load/CPU observations are retained in `environment-numeric-public.json`; removing private process command lines does not make the experiment clean.
M1 `manifest.json` differs only by removal of `environment.processSnapshot`; original manifest remains local. Its measurement settings, identities, execution plan and analysis inputs are retained. See [redactions.json](redactions.json).
Build/package caches, symbolic links and MSBuild binary logs (which may contain ambient environment values) are excluded. Text diagnostics, actual binaries, source snapshots and existing provenance are retained. Product/R1 source is published separately; the local Git bundle is not mirrored.
Credential-pattern and nested-archive scans are recorded in [scan-review.json](scan-review.json). Exact excluded file paths and reasons follow and are also available in [excluded-files.json](excluded-files.json).

- `931-original-48/ownership-931-evidence/baseline/build.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-original-48/ownership-931-evidence/baseline/pack-Abstractions.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-original-48/ownership-931-evidence/baseline/pack-Client.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-original-48/ownership-931-evidence/baseline/pack-Compression.Zstd.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-original-48/ownership-931-evidence/baseline/pack-GenerationControl.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-original-48/ownership-931-evidence/baseline/pack-Hosting.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-original-48/ownership-931-evidence/baseline/pack-Runtime.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-original-48/ownership-931-evidence/baseline/pack-Sdk.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-original-48/ownership-931-evidence/baseline/pack-Serializer.SharpPack.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-original-48/ownership-931-evidence/baseline/pack-Server.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-original-48/ownership-931-evidence/candidate/build.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-original-48/ownership-931-evidence/candidate/pack-Abstractions.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-original-48/ownership-931-evidence/candidate/pack-Client.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-original-48/ownership-931-evidence/candidate/pack-Compression.Zstd.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-original-48/ownership-931-evidence/candidate/pack-GenerationControl.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-original-48/ownership-931-evidence/candidate/pack-Hosting.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-original-48/ownership-931-evidence/candidate/pack-Runtime.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-original-48/ownership-931-evidence/candidate/pack-Sdk.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-original-48/ownership-931-evidence/candidate/pack-Serializer.SharpPack.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-original-48/ownership-931-evidence/candidate/pack-Server.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-original-48/ownership-931-evidence/scalar-prepared/build/obj/Release/net10.0/SharpLink.Benchmarks.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/scalar-prepared/build/obj/Release/net10.0/SharpLink.Benchmarks.pdb` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/scalar-prepared/build/obj/Release/net10.0/apphost` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/scalar-prepared/build/obj/Release/net10.0/probe.AssemblyInfo.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/scalar-prepared/build/obj/Release/net10.0/probe.AssemblyInfoInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/scalar-prepared/build/obj/Release/net10.0/probe.GeneratedMSBuildEditorConfig.editorconfig` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/scalar-prepared/build/obj/Release/net10.0/probe.GlobalUsings.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/scalar-prepared/build/obj/Release/net10.0/probe.assets.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/scalar-prepared/build/obj/Release/net10.0/probe.csproj.AssemblyReference.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/scalar-prepared/build/obj/Release/net10.0/probe.csproj.CoreCompileInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/scalar-prepared/build/obj/Release/net10.0/probe.csproj.FileListAbsolute.txt` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/scalar-prepared/build/obj/Release/net10.0/probe.csproj.Up2Date` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/scalar-prepared/build/obj/Release/net10.0/probe.genruntimeconfig.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/scalar-prepared/build/obj/Release/net10.0/ref/SharpLink.Benchmarks.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/scalar-prepared/build/obj/Release/net10.0/refint/SharpLink.Benchmarks.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/scalar-prepared/build/obj/probe.csproj.nuget.dgspec.json` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/scalar-prepared/build/obj/probe.csproj.nuget.g.props` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/scalar-prepared/build/obj/probe.csproj.nuget.g.targets` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/scalar-prepared/build/obj/project.assets.json` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/scalar-prepared/build/obj/project.nuget.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/scalar-prepared/build.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-original-48/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/ScalarStreamModule.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/ScalarStreamModule.pdb` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.AssemblyInfo.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.AssemblyInfoInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.GeneratedMSBuildEditorConfig.editorconfig` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.GlobalUsings.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.assets.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.csproj.AssemblyReference.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.csproj.CoreCompileInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.csproj.FileListAbsolute.txt` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.csproj.Up2Date` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/ref/ScalarStreamModule.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/refint/ScalarStreamModule.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/module/obj/generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/PhysicalTcp_IScalarStream_74CC6455923B5D5E_Proxy.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/module/obj/generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/PhysicalTcp_IScalarStream_74CC6455923B5D5E_ProxyImpl.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/module/obj/generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/PhysicalTcp_IScalarStream_74CC6455923B5D5E_Stub.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/module/obj/generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/SharpLink.ContractManifest.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/module/obj/generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/SharpLink.GeneratedAssemblyManifest.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/module/obj/module.csproj.nuget.dgspec.json` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/module/obj/module.csproj.nuget.g.props` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/module/obj/module.csproj.nuget.g.targets` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/module/obj/project.assets.json` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/module/obj/project.nuget.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/PhysicalTcpProbe.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/PhysicalTcpProbe.pdb` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/apphost` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.AssemblyInfo.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.AssemblyInfoInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.GeneratedMSBuildEditorConfig.editorconfig` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.GlobalUsings.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.assets.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.csproj.AssemblyReference.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.csproj.CoreCompileInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.csproj.FileListAbsolute.txt` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.csproj.Up2Date` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.genruntimeconfig.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/ref/PhysicalTcpProbe.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/refint/PhysicalTcpProbe.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/probe/obj/generated/Microsoft.AspNetCore.App.SourceGenerators/Microsoft.AspNetCore.SourceGenerators.PublicProgramSourceGenerator/PublicTopLevelProgram.Generated.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/probe/obj/probe.csproj.nuget.dgspec.json` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/probe/obj/probe.csproj.nuget.g.props` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/probe/obj/probe.csproj.nuget.g.targets` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/probe/obj/project.assets.json` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/build/probe/obj/project.nuget.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-original-48/ownership-931-evidence/tcp-prepared/module-build.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-original-48/ownership-931-evidence/tcp-prepared/probe-build.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/baseline/build.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/baseline/pack-Abstractions.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/baseline/pack-Client.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/baseline/pack-Compression.Zstd.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/baseline/pack-GenerationControl.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/baseline/pack-Hosting.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/baseline/pack-Runtime.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/baseline/pack-Sdk.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/baseline/pack-Serializer.SharpPack.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/baseline/pack-Server.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/candidate/build.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/candidate/pack-Abstractions.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/candidate/pack-Client.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/candidate/pack-Compression.Zstd.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/candidate/pack-GenerationControl.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/candidate/pack-Hosting.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/candidate/pack-Runtime.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/candidate/pack-Sdk.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/candidate/pack-Serializer.SharpPack.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/candidate/pack-Server.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/ScalarStreamModule.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/ScalarStreamModule.pdb` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.AssemblyInfo.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.AssemblyInfoInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.GeneratedMSBuildEditorConfig.editorconfig` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.GlobalUsings.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.assets.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.csproj.AssemblyReference.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.csproj.CoreCompileInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.csproj.FileListAbsolute.txt` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.csproj.Up2Date` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/ref/ScalarStreamModule.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/refint/ScalarStreamModule.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/module/obj/generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/PhysicalTcp_IScalarStream_74CC6455923B5D5E_Proxy.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/module/obj/generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/PhysicalTcp_IScalarStream_74CC6455923B5D5E_ProxyImpl.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/module/obj/generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/PhysicalTcp_IScalarStream_74CC6455923B5D5E_Stub.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/module/obj/generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/SharpLink.ContractManifest.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/module/obj/generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/SharpLink.GeneratedAssemblyManifest.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/module/obj/module.csproj.nuget.dgspec.json` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/module/obj/module.csproj.nuget.g.props` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/module/obj/module.csproj.nuget.g.targets` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/module/obj/project.assets.json` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/module/obj/project.nuget.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/PhysicalTcpProbe.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/PhysicalTcpProbe.pdb` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/apphost` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.AssemblyInfo.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.AssemblyInfoInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.GeneratedMSBuildEditorConfig.editorconfig` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.GlobalUsings.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.assets.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.csproj.AssemblyReference.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.csproj.CoreCompileInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.csproj.FileListAbsolute.txt` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.csproj.Up2Date` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.genruntimeconfig.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/ref/PhysicalTcpProbe.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/refint/PhysicalTcpProbe.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/probe/obj/generated/Microsoft.AspNetCore.App.SourceGenerators/Microsoft.AspNetCore.SourceGenerators.PublicProgramSourceGenerator/PublicTopLevelProgram.Generated.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/probe/obj/probe.csproj.nuget.dgspec.json` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/probe/obj/probe.csproj.nuget.g.props` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/probe/obj/probe.csproj.nuget.g.targets` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/probe/obj/project.assets.json` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/build/probe/obj/project.nuget.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/module-build.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-fullopts-12/physical-ownership-931-client-ablation-dd02ecfc6970cfa3b621d919d48e4d9e48f205aa-1/ownership-931-evidence/tcp-prepared/probe-build.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/A-app.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/A-preflight.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/B-app.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/B-preflight.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/O-app.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/O-preflight.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/app/obj/CausalProbe.csproj.nuget.dgspec.json` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/app/obj/CausalProbe.csproj.nuget.g.props` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/app/obj/CausalProbe.csproj.nuget.g.targets` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/app/obj/Generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/MethodFactsExperiment_IFactsRpc_3AEDDF623EF2FF8F_Proxy.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/app/obj/Generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/MethodFactsExperiment_IFactsRpc_3AEDDF623EF2FF8F_ProxyImpl.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/app/obj/Generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/MethodFactsExperiment_IFactsRpc_3AEDDF623EF2FF8F_Stub.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/app/obj/Generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/SharpLink.ContractManifest.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/app/obj/Generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/SharpLink.GeneratedAssemblyManifest.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/app/obj/Release/net10.0/CausalPr.8D163FC2.Up2Date` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/app/obj/Release/net10.0/CausalProbe.AssemblyInfo.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/app/obj/Release/net10.0/CausalProbe.AssemblyInfoInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/app/obj/Release/net10.0/CausalProbe.GeneratedMSBuildEditorConfig.editorconfig` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/app/obj/Release/net10.0/CausalProbe.GlobalUsings.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/app/obj/Release/net10.0/CausalProbe.assets.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/app/obj/Release/net10.0/CausalProbe.csproj.AssemblyReference.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/app/obj/Release/net10.0/CausalProbe.csproj.CoreCompileInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/app/obj/Release/net10.0/CausalProbe.csproj.FileListAbsolute.txt` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/app/obj/Release/net10.0/CausalProbe.dll` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/app/obj/Release/net10.0/CausalProbe.genruntimeconfig.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/app/obj/Release/net10.0/CausalProbe.pdb` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/app/obj/Release/net10.0/PublishOutputs.574901d69c.txt` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/app/obj/Release/net10.0/apphost` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/app/obj/Release/net10.0/ref/CausalProbe.dll` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/app/obj/Release/net10.0/refint/CausalProbe.dll` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/app/obj/project.assets.json` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/app/obj/project.nuget.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/preflight/obj/Preflight.csproj.nuget.dgspec.json` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/preflight/obj/Preflight.csproj.nuget.g.props` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/preflight/obj/Preflight.csproj.nuget.g.targets` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/preflight/obj/Release/net10.0/Preflight.AssemblyInfo.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/preflight/obj/Release/net10.0/Preflight.AssemblyInfoInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/preflight/obj/Release/net10.0/Preflight.GeneratedMSBuildEditorConfig.editorconfig` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/preflight/obj/Release/net10.0/Preflight.GlobalUsings.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/preflight/obj/Release/net10.0/Preflight.assets.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/preflight/obj/Release/net10.0/Preflight.csproj.AssemblyReference.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/preflight/obj/Release/net10.0/Preflight.csproj.CoreCompileInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/preflight/obj/Release/net10.0/Preflight.csproj.FileListAbsolute.txt` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/preflight/obj/Release/net10.0/Preflight.csproj.Up2Date` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/preflight/obj/Release/net10.0/Preflight.dll` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/preflight/obj/Release/net10.0/Preflight.genruntimeconfig.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/preflight/obj/Release/net10.0/Preflight.pdb` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/preflight/obj/Release/net10.0/PublishOutputs.f360ddaca7.txt` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/preflight/obj/Release/net10.0/apphost` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/preflight/obj/Release/net10.0/ref/Preflight.dll` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/preflight/obj/Release/net10.0/refint/Preflight.dll` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/preflight/obj/project.assets.json` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/A/preflight/obj/project.nuget.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/app/obj/CausalProbe.csproj.nuget.dgspec.json` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/app/obj/CausalProbe.csproj.nuget.g.props` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/app/obj/CausalProbe.csproj.nuget.g.targets` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/app/obj/Generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/MethodFactsExperiment_IFactsRpc_3AEDDF623EF2FF8F_Proxy.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/app/obj/Generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/MethodFactsExperiment_IFactsRpc_3AEDDF623EF2FF8F_ProxyImpl.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/app/obj/Generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/MethodFactsExperiment_IFactsRpc_3AEDDF623EF2FF8F_Stub.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/app/obj/Generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/SharpLink.ContractManifest.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/app/obj/Generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/SharpLink.GeneratedAssemblyManifest.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/app/obj/Release/net10.0/CausalPr.8D163FC2.Up2Date` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/app/obj/Release/net10.0/CausalProbe.AssemblyInfo.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/app/obj/Release/net10.0/CausalProbe.AssemblyInfoInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/app/obj/Release/net10.0/CausalProbe.GeneratedMSBuildEditorConfig.editorconfig` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/app/obj/Release/net10.0/CausalProbe.GlobalUsings.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/app/obj/Release/net10.0/CausalProbe.assets.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/app/obj/Release/net10.0/CausalProbe.csproj.AssemblyReference.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/app/obj/Release/net10.0/CausalProbe.csproj.CoreCompileInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/app/obj/Release/net10.0/CausalProbe.csproj.FileListAbsolute.txt` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/app/obj/Release/net10.0/CausalProbe.dll` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/app/obj/Release/net10.0/CausalProbe.genruntimeconfig.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/app/obj/Release/net10.0/CausalProbe.pdb` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/app/obj/Release/net10.0/PublishOutputs.09c9cc1a90.txt` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/app/obj/Release/net10.0/apphost` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/app/obj/Release/net10.0/ref/CausalProbe.dll` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/app/obj/Release/net10.0/refint/CausalProbe.dll` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/app/obj/project.assets.json` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/app/obj/project.nuget.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/preflight/obj/Preflight.csproj.nuget.dgspec.json` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/preflight/obj/Preflight.csproj.nuget.g.props` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/preflight/obj/Preflight.csproj.nuget.g.targets` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/preflight/obj/Release/net10.0/Preflight.AssemblyInfo.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/preflight/obj/Release/net10.0/Preflight.AssemblyInfoInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/preflight/obj/Release/net10.0/Preflight.GeneratedMSBuildEditorConfig.editorconfig` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/preflight/obj/Release/net10.0/Preflight.GlobalUsings.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/preflight/obj/Release/net10.0/Preflight.assets.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/preflight/obj/Release/net10.0/Preflight.csproj.AssemblyReference.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/preflight/obj/Release/net10.0/Preflight.csproj.CoreCompileInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/preflight/obj/Release/net10.0/Preflight.csproj.FileListAbsolute.txt` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/preflight/obj/Release/net10.0/Preflight.csproj.Up2Date` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/preflight/obj/Release/net10.0/Preflight.dll` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/preflight/obj/Release/net10.0/Preflight.genruntimeconfig.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/preflight/obj/Release/net10.0/Preflight.pdb` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/preflight/obj/Release/net10.0/PublishOutputs.1830ad0fbe.txt` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/preflight/obj/Release/net10.0/apphost` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/preflight/obj/Release/net10.0/ref/Preflight.dll` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/preflight/obj/Release/net10.0/refint/Preflight.dll` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/preflight/obj/project.assets.json` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/B/preflight/obj/project.nuget.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/app/obj/CausalProbe.csproj.nuget.dgspec.json` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/app/obj/CausalProbe.csproj.nuget.g.props` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/app/obj/CausalProbe.csproj.nuget.g.targets` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/app/obj/Generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/MethodFactsExperiment_IFactsRpc_3AEDDF623EF2FF8F_Proxy.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/app/obj/Generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/MethodFactsExperiment_IFactsRpc_3AEDDF623EF2FF8F_ProxyImpl.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/app/obj/Generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/MethodFactsExperiment_IFactsRpc_3AEDDF623EF2FF8F_Stub.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/app/obj/Generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/SharpLink.ContractManifest.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/app/obj/Generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/SharpLink.GeneratedAssemblyManifest.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/app/obj/Release/net10.0/CausalPr.8D163FC2.Up2Date` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/app/obj/Release/net10.0/CausalProbe.AssemblyInfo.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/app/obj/Release/net10.0/CausalProbe.AssemblyInfoInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/app/obj/Release/net10.0/CausalProbe.GeneratedMSBuildEditorConfig.editorconfig` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/app/obj/Release/net10.0/CausalProbe.GlobalUsings.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/app/obj/Release/net10.0/CausalProbe.assets.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/app/obj/Release/net10.0/CausalProbe.csproj.AssemblyReference.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/app/obj/Release/net10.0/CausalProbe.csproj.CoreCompileInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/app/obj/Release/net10.0/CausalProbe.csproj.FileListAbsolute.txt` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/app/obj/Release/net10.0/CausalProbe.dll` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/app/obj/Release/net10.0/CausalProbe.genruntimeconfig.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/app/obj/Release/net10.0/CausalProbe.pdb` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/app/obj/Release/net10.0/PublishOutputs.84639cf78b.txt` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/app/obj/Release/net10.0/apphost` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/app/obj/Release/net10.0/ref/CausalProbe.dll` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/app/obj/Release/net10.0/refint/CausalProbe.dll` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/app/obj/project.assets.json` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/app/obj/project.nuget.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/preflight/obj/Preflight.csproj.nuget.dgspec.json` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/preflight/obj/Preflight.csproj.nuget.g.props` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/preflight/obj/Preflight.csproj.nuget.g.targets` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/preflight/obj/Release/net10.0/Preflight.AssemblyInfo.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/preflight/obj/Release/net10.0/Preflight.AssemblyInfoInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/preflight/obj/Release/net10.0/Preflight.GeneratedMSBuildEditorConfig.editorconfig` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/preflight/obj/Release/net10.0/Preflight.GlobalUsings.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/preflight/obj/Release/net10.0/Preflight.assets.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/preflight/obj/Release/net10.0/Preflight.csproj.AssemblyReference.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/preflight/obj/Release/net10.0/Preflight.csproj.CoreCompileInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/preflight/obj/Release/net10.0/Preflight.csproj.FileListAbsolute.txt` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/preflight/obj/Release/net10.0/Preflight.csproj.Up2Date` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/preflight/obj/Release/net10.0/Preflight.dll` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/preflight/obj/Release/net10.0/Preflight.genruntimeconfig.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/preflight/obj/Release/net10.0/Preflight.pdb` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/preflight/obj/Release/net10.0/PublishOutputs.9187c6bee5.txt` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/preflight/obj/Release/net10.0/apphost` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/preflight/obj/Release/net10.0/ref/Preflight.dll` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/preflight/obj/Release/net10.0/refint/Preflight.dll` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/preflight/obj/project.assets.json` — Build/package/runtime cache; not raw measurement evidence
- `732-hosted-three-arm-36/method-facts-732-47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f-1/method-facts-732-evidence/build/O/preflight/obj/project.nuget.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/baseline/build.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/baseline/pack-Abstractions.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/baseline/pack-Client.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/baseline/pack-Compression.Zstd.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/baseline/pack-GenerationControl.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/baseline/pack-Hosting.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/baseline/pack-Runtime.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/baseline/pack-Sdk.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/baseline/pack-Serializer.SharpPack.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/baseline/pack-Server.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/candidate/build.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/candidate/pack-Abstractions.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/candidate/pack-Client.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/candidate/pack-Compression.Zstd.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/candidate/pack-GenerationControl.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/candidate/pack-Hosting.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/candidate/pack-Runtime.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/candidate/pack-Sdk.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/candidate/pack-Serializer.SharpPack.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/candidate/pack-Server.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/ScalarStreamModule.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/ScalarStreamModule.pdb` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.AssemblyInfo.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.AssemblyInfoInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.GeneratedMSBuildEditorConfig.editorconfig` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.GlobalUsings.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.assets.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.csproj.AssemblyReference.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.csproj.CoreCompileInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.csproj.FileListAbsolute.txt` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/module.csproj.Up2Date` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/ref/ScalarStreamModule.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/module/obj/Release/net10.0/refint/ScalarStreamModule.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/module/obj/generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/PhysicalTcp_IScalarStream_74CC6455923B5D5E_Proxy.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/module/obj/generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/PhysicalTcp_IScalarStream_74CC6455923B5D5E_ProxyImpl.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/module/obj/generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/PhysicalTcp_IScalarStream_74CC6455923B5D5E_Stub.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/module/obj/generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/SharpLink.ContractManifest.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/module/obj/generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/SharpLink.GeneratedAssemblyManifest.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/module/obj/module.csproj.nuget.dgspec.json` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/module/obj/module.csproj.nuget.g.props` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/module/obj/module.csproj.nuget.g.targets` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/module/obj/project.assets.json` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/module/obj/project.nuget.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/PhysicalTcpProbe.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/PhysicalTcpProbe.pdb` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/apphost` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.AssemblyInfo.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.AssemblyInfoInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.GeneratedMSBuildEditorConfig.editorconfig` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.GlobalUsings.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.assets.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.csproj.AssemblyReference.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.csproj.CoreCompileInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.csproj.FileListAbsolute.txt` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.csproj.Up2Date` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/probe.genruntimeconfig.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/ref/PhysicalTcpProbe.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/probe/obj/Release/net10.0/refint/PhysicalTcpProbe.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/probe/obj/generated/Microsoft.AspNetCore.App.SourceGenerators/Microsoft.AspNetCore.SourceGenerators.PublicProgramSourceGenerator/PublicTopLevelProgram.Generated.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/probe/obj/probe.csproj.nuget.dgspec.json` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/probe/obj/probe.csproj.nuget.g.props` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/probe/obj/probe.csproj.nuget.g.targets` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/probe/obj/project.assets.json` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/build/probe/obj/project.nuget.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/module-build.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `931-pgo-12/physical-ownership-931-client-ablation-pgo-51f5bd0ec3f600196f2e26ea48a466ec2cea62d8-1/ownership-931-evidence/tcp-prepared/probe-build.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `732-m1-local/build-A/obj/CausalProbe.csproj.nuget.dgspec.json` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A/obj/CausalProbe.csproj.nuget.g.props` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A/obj/CausalProbe.csproj.nuget.g.targets` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A/obj/Generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/MethodFactsExperiment_IFactsRpc_3AEDDF623EF2FF8F_Proxy.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A/obj/Generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/MethodFactsExperiment_IFactsRpc_3AEDDF623EF2FF8F_ProxyImpl.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A/obj/Generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/MethodFactsExperiment_IFactsRpc_3AEDDF623EF2FF8F_Stub.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A/obj/Generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/SharpLink.ContractManifest.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A/obj/Generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/SharpLink.GeneratedAssemblyManifest.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A/obj/Release/net10.0/.NETCoreApp,Version=v10.0.AssemblyAttributes.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A/obj/Release/net10.0/CausalPr.8D163FC2.Up2Date` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A/obj/Release/net10.0/CausalProbe.AssemblyInfo.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A/obj/Release/net10.0/CausalProbe.AssemblyInfoInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A/obj/Release/net10.0/CausalProbe.GeneratedMSBuildEditorConfig.editorconfig` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A/obj/Release/net10.0/CausalProbe.GlobalUsings.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A/obj/Release/net10.0/CausalProbe.assets.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A/obj/Release/net10.0/CausalProbe.csproj.AssemblyReference.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A/obj/Release/net10.0/CausalProbe.csproj.CoreCompileInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A/obj/Release/net10.0/CausalProbe.csproj.FileListAbsolute.txt` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A/obj/Release/net10.0/CausalProbe.dll` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A/obj/Release/net10.0/CausalProbe.genruntimeconfig.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A/obj/Release/net10.0/CausalProbe.pdb` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A/obj/Release/net10.0/CausalProbe.sourcelink.json` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A/obj/Release/net10.0/apphost` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A/obj/Release/net10.0/ref/CausalProbe.dll` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A/obj/Release/net10.0/refint/CausalProbe.dll` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A/obj/project.assets.json` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A/obj/project.nuget.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-A-20261028-050111--24506--TYj5wV.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `732-m1-local/build-A-20261028-050155--24802--a1Nzfc.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `732-m1-local/build-A-20261028-050438--25482--rgganW.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `732-m1-local/build-B/obj/CausalProbe.csproj.nuget.dgspec.json` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B/obj/CausalProbe.csproj.nuget.g.props` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B/obj/CausalProbe.csproj.nuget.g.targets` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B/obj/Generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/MethodFactsExperiment_IFactsRpc_3AEDDF623EF2FF8F_Proxy.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B/obj/Generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/MethodFactsExperiment_IFactsRpc_3AEDDF623EF2FF8F_ProxyImpl.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B/obj/Generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/MethodFactsExperiment_IFactsRpc_3AEDDF623EF2FF8F_Stub.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B/obj/Generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/SharpLink.ContractManifest.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B/obj/Generated/SharpLink.Generator/SharpLink.Generator.RpcGenerator/SharpLink.GeneratedAssemblyManifest.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B/obj/Release/net10.0/.NETCoreApp,Version=v10.0.AssemblyAttributes.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B/obj/Release/net10.0/CausalPr.8D163FC2.Up2Date` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B/obj/Release/net10.0/CausalProbe.AssemblyInfo.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B/obj/Release/net10.0/CausalProbe.AssemblyInfoInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B/obj/Release/net10.0/CausalProbe.GeneratedMSBuildEditorConfig.editorconfig` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B/obj/Release/net10.0/CausalProbe.GlobalUsings.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B/obj/Release/net10.0/CausalProbe.assets.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B/obj/Release/net10.0/CausalProbe.csproj.AssemblyReference.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B/obj/Release/net10.0/CausalProbe.csproj.CoreCompileInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B/obj/Release/net10.0/CausalProbe.csproj.FileListAbsolute.txt` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B/obj/Release/net10.0/CausalProbe.dll` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B/obj/Release/net10.0/CausalProbe.genruntimeconfig.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B/obj/Release/net10.0/CausalProbe.pdb` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B/obj/Release/net10.0/CausalProbe.sourcelink.json` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B/obj/Release/net10.0/apphost` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B/obj/Release/net10.0/ref/CausalProbe.dll` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B/obj/Release/net10.0/refint/CausalProbe.dll` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B/obj/project.assets.json` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B/obj/project.nuget.cache` — Build/package/runtime cache; not raw measurement evidence
- `732-m1-local/build-B-20261028-050157--24811--DC1ZXb.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `732-m1-local/build-B-20261028-050443--25506--qljoeH.binlog` — MSBuild binary logs can embed ambient environment; corresponding text logs and binary/source provenance retained
- `732-m1-local/environment-during.jsonl` — Original process snapshots contain unrelated command lines; local originals preserved; numeric-only derivative supplied
- `732-m1-local/environment-summary.json` — Original process snapshots contain unrelated command lines; local originals preserved; numeric-only derivative supplied
- `732-m1-local/start-state.json` — Original process snapshots contain unrelated command lines; local originals preserved; numeric-only derivative supplied
- `931-r1-loop-exit/inspect-source/obj/Release/net10.0/.NETCoreApp,Version=v10.0.AssemblyAttributes.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/inspect-source/obj/Release/net10.0/apphost` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/inspect-source/obj/Release/net10.0/inspect.AssemblyInfo.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/inspect-source/obj/Release/net10.0/inspect.AssemblyInfoInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/inspect-source/obj/Release/net10.0/inspect.GeneratedMSBuildEditorConfig.editorconfig` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/inspect-source/obj/Release/net10.0/inspect.GlobalUsings.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/inspect-source/obj/Release/net10.0/inspect.assets.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/inspect-source/obj/Release/net10.0/inspect.csproj.AssemblyReference.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/inspect-source/obj/Release/net10.0/inspect.csproj.CoreCompileInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/inspect-source/obj/Release/net10.0/inspect.csproj.FileListAbsolute.txt` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/inspect-source/obj/Release/net10.0/inspect.csproj.Up2Date` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/inspect-source/obj/Release/net10.0/inspect.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/inspect-source/obj/Release/net10.0/inspect.genruntimeconfig.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/inspect-source/obj/Release/net10.0/inspect.pdb` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/inspect-source/obj/Release/net10.0/inspect.sourcelink.json` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/inspect-source/obj/Release/net10.0/ref/inspect.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/inspect-source/obj/Release/net10.0/refint/inspect.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/inspect-source/obj/inspect.csproj.nuget.dgspec.json` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/inspect-source/obj/inspect.csproj.nuget.g.props` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/inspect-source/obj/inspect.csproj.nuget.g.targets` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/inspect-source/obj/project.assets.json` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/inspect-source/obj/project.nuget.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/scalar-obj/Release/net10.0/.NETCoreApp,Version=v10.0.AssemblyAttributes.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/scalar-obj/Release/net10.0/SharpLink.Benchmarks.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/scalar-obj/Release/net10.0/SharpLink.Benchmarks.pdb` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/scalar-obj/Release/net10.0/apphost` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/scalar-obj/Release/net10.0/probe.AssemblyInfo.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/scalar-obj/Release/net10.0/probe.AssemblyInfoInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/scalar-obj/Release/net10.0/probe.GeneratedMSBuildEditorConfig.editorconfig` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/scalar-obj/Release/net10.0/probe.GlobalUsings.g.cs` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/scalar-obj/Release/net10.0/probe.assets.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/scalar-obj/Release/net10.0/probe.csproj.AssemblyReference.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/scalar-obj/Release/net10.0/probe.csproj.CoreCompileInputs.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/scalar-obj/Release/net10.0/probe.csproj.FileListAbsolute.txt` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/scalar-obj/Release/net10.0/probe.csproj.Up2Date` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/scalar-obj/Release/net10.0/probe.genruntimeconfig.cache` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/scalar-obj/Release/net10.0/probe.sourcelink.json` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/scalar-obj/Release/net10.0/ref/SharpLink.Benchmarks.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/scalar-obj/Release/net10.0/refint/SharpLink.Benchmarks.dll` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/scalar-obj/probe.csproj.nuget.dgspec.json` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/scalar-obj/probe.csproj.nuget.g.props` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/scalar-obj/probe.csproj.nuget.g.targets` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/scalar-obj/project.assets.json` — Build/package/runtime cache; not raw measurement evidence
- `931-r1-loop-exit/scalar-obj/project.nuget.cache` — Build/package/runtime cache; not raw measurement evidence

## Hosted R1 native and boundary-test archive

[r1-hosted-native-40681832353](archives/r1-hosted-native-40681832353.tar.gz) retains the independently validated
[run40681832353](https://github.com/SunSi12138/SharpLink/actions/runs/40681832353),
exact tool `9c0cf9e682deb38c4c321e93db0f11d1338bc7bd`, plus8focused boundary-test
results at test-only `c4cb5771dff994f9cd3db8252582d3c0256f7c09` (src identical to94).
Both4352-operation non-timed processes satisfy4Tier1/source/runtime gates. The
success-path duplicate test is removed; long jump remains and native body grows.
This is **not** a performance result, no-regression proof or full candidate CI.
#931NoGo and all earlier failed cloud attempts remain unchanged.

SHA256 `03e8e6f4bb7479f1df0943a08d3072e7a91164c76856d5d0834594e8faa66a27`,3,369,522 bytes/98 files. Its
[file/privacy inventory](r1-hosted-native-40681832353-files.json) and embedded
`official-artifact-members.json` enumerate every included member plus EXACTLY2
excluded official runtime installer tarballs (both hashes/URLs pinned and retained).
No raw native/host/identity/proof/test evidence is sanitized or omitted. The original
89,150,325-byte officialZIP digest is retained; it is not duplicated as a container.
Independent report/verifier and8 extracted target bodies are included unchanged.

## Full-product R1 diagnostic archive (lossless split storage)

[Run40687272444](https://github.com/SunSi12138/SharpLink/actions/runs/40687272444),
exact tool `271103147577a70bd973adfac1860f2da2276f07`, completed the frozen original
48TCP+14scalar process protocol for coherent cc7 baseline versus94ade R1. Independent
raw review confirms2,763,672ticks, identities, schedule and descriptive statistics.
Dynamic1 P99 is+7.06% (all6pairs adverse), P99.9+10.60%; remaining cells are mixed/noisy.
**No broad recovery or no-regression proof; #931NoGo remains.** No samples were added,
replaced or removed. Earlier original/cloud failures remain in their prior archives.

The [ordered parts manifest](archives/r1-full-product-40687272444.parts/manifest.json)
records all11parts' order, exact lengths and SHA256. They are binary slices of one
already-reviewed gzip archive, **not** independent tarballs. Reassemble using the
commands above, verify every part and the total, then extract the reconstructed file.
Reassembled length34,353,133 bytes; SHA256
`892b8cfcfcc50da4cf22b4046ea0228ba31f2ecde04c783421760df3288ba23b`.
No monolithic duplicate is stored in Git. Splitting addresses conservative connector
payload sizing only and changes no archive byte or member. Existing12archives keep
their original storage paths and hashes. No failed upload was hidden or retried.

The [611-member inventory](r1-full-product-40687272444-files.json) retains all raw
measurements, prepared binaries, package/build/host/proof logs and independent reports.
Exactly2official framework installer tarballs are excluded from the original ZIP;
all594other original members are byte-identical, with full original ZIP/member hashes,
installer SHA512/URLs and exclusion reasons retained. The approved compact archive
also contains the unchanged independent report/scripts and supplemental owner audit.
Independent privacy review checked all611members,23decompressedbinlogs and240package
ZIP members with no credential findings. None of the included raw evidence is sanitized.

## Published source branches

- R1 loop-exit source: [codex/931-loop-exit-experiment](https://github.com/SunSi12138/SharpLink/tree/codex/931-loop-exit-experiment).
- `index.json` lists original identity files and their declared commit/SHA fields without reinterpreting them as this mirror commit.


## Phase rewrite607: actual local + hosted measurements (2026-10-28)

Candidate is **not promoted / not merged**: actual bounded measurements do not establish throughput/CPU benefit and preserve descriptive tail risk, not because native code is larger. Both independent raw-evidence reviews PASS, which is evidence validity rather than performanceGo. [Hosted independent review](phase-rewrite-performance-20261028-hosted-review.md) and [separate local independent review](phase-rewrite-performance-20261028-local-review.md).

- [Draft PR963](https://github.com/SunSi12138/SharpLink/pull/963), [hosted run40712925050/attempt1](https://github.com/SunSi12138/SharpLink/actions/runs/40712925050). Baselineac17 vs actual built2153/exact607tree, NOT official2.0.3.
- Each environment separately completed48TCP processes/24pairs+14scalar. Hosted3,551,642RPCs; local4,137,425RPCs. No statistical pooling, discarded pair, timed retry or added sample.
- Hosted QPS pairedmean changes static1/static100/dynamic1/dynamic100:−0.01/−0.35/−1.42/−0.51%; all QPS/CPU/P99 descriptive95% intervals cross zero. Hostedstatic1P99.9+24.28%[+2.80,+52.72]% is an adverse descriptive signal, not multiplicity-adjusted formal proof. All6ratios retained. Local performance mixed/inconclusive. Scalar allocation12cells×5pairs identical in each environment.
- Hosted first2logicalCPUs are same-coreSMTsiblings; local sysfs/cgroup/physical isolation unknown, gueststeal1.474–6.854%. Sixpairs/cell, fixed nonrandom order, defaulttiering and one-worker loopback shape limit inference. No equivalence/release acceptance or code-size gate.
- [Ordered-parts manifest](archives/phase-rewrite-performance-20261028.parts/manifest.json):30 binary slices≤3MiB; logical archive93,087,850bytes, SHA256`8c1cd1ff0f165eba1d0912d5fbe75604030c8ce895cf359f15b38b260dab6454`;5072members/207,809,550uncompressed bytes. The fail-closed procedure at the top explicitly processes every index entry, including this phase manifest; it reconstructs this archive under the fresh `verified-evidence.XXXXXX/archives/` directory and extracts it separately only after all archive checks pass.
- [Complete member/privacy/exclusion inventory](phase-rewrite-performance-20261028-files.json). All594included hostedZIP members exactly preserved,2official runtime installers separately hashed/excluded; originalZIP transport hash/member map retained. Local raw, all prepare-only failures, prior8-process mechanism evidence including4nativecompleteness failures, quiet receipts, complete product source trees, tool sources and both independent reports included without redaction. Earlier STOP/no-timing text is historical and superseded by user-requested measurements, not a current performance premise.
- No previous archive bytes or entries were replaced. This is diagnostic evidence only; it does not authorize merging or releasing product code.
