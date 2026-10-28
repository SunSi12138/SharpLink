# #931 R1 cloud native result: incomplete four-target gate

2026-10-28. Product R1 remains `94adeae886c4ddfd0c02ec2dcb700cd97e9cad80`.
Executed tool `17038d5014d1d4a76407fa13c451d6a0606968ff` was independently
approved before execution. No product changes, timing campaign, merge or release.
**Original #931 NoGo remains. This run did not pass the complete mechanism gate.**

## Outcomes, including every failed preparation

| Attempt | Exact tool | Outcome |
|---|---|---|
| 001 | 573c51331059f6cf453cbd2e903aebafe7f85531 | Stopped before build/probe: cloud has no `/sys/devices/system/cpu`, required by inherited topology helper. |
| 002 | 17038d5014d1d4a76407fa13c451d6a0606968ff | Strict procfs topology/affinity valid. Build restore failed with NU1900 because NuGet used read-only home cache; no probe ran. |
| 003 | same17038d5 | Cache paths redirected inside writable workspace without audit suppression. Build passed0warnings/0errors. Two fixed processes each exit0 and4352RPC, but both native gates Failed. |

Attempt001 is retained; the narrow procfs fallback was independently reviewed
before002. Attempt002 logs remain intact. Attempt003 changed only cache locations,
not source/runtime/workload/gates. No failed native sample was retried/replaced.
All process invocations used the unchanged reviewed probe from tool51f5bd0e.

## Executing identities

- Linux x64 shared cloud, reported AMD EPYC9V74. Explicit procfs package/core
  identities cover allowed CPUs0–8; affinity0,1 verified. No sysfs/independent
  physical or SMT verification is claimed. Each .NET process reported2CPUs.
- Build SDK10.0.102; private runtime-only .NET + ASP.NET10.0.13, official archives
  verified against retained Microsoft's release-metadata SHA512 before extraction.
- Both snapshots reuse original run40646151477 candidate common product/probe/module
  bytes. Their entire DLL inventories are identical except SharpLink.Client.dll.
- Baseline Client product version3.0.0+ac17c8d8ea0203edbe7b8cf19ddb95eaa24d4d2c,
  SHA256`0686f359f96cfbda10355f5d17fd14d33dafdc321130ba9fc1e8cb63f87cb4de`.
- R1 Client product version3.0.0+94adeae886c4ddfd0c02ec2dcb700cd97e9cad80,
  SHA256`e85d1c68dd1070427c6c520a0cad87d4e791370bd38e39f536b25b2379de8f94`.
- Both loaded Runtime/Server/other product assemblies retain ac17 source stamp;
  probe/module bytes are the same reviewed archive. Loaded paths, source stamps
  and complete executing DLL hashes were revalidated from untouched raw proofs.

## Four-target gate

Both processes used default tiering/PGO, one ready TCP connection, one worker,
100items, exactly4352RPCs and fixed250ms pauses after1024/2048/3072/4096.
No throughput, latency or timed-call arrays were collected.

| Actual closed-generic MoveNext | ac17 | R1 |
|---|---:|---:|
| intercepted iterator | Tier1,4703B | Tier1,5645B |
| dynamic request iterator | Tier1,2345B | Tier1,3298B |
| Runtime outbound pump | Tier0/instrumented only; requiredTier1 missing | Same missingTier1 |
| Server stream bridge | Tier0/instrumented only; requiredTier1 missing | Same missingTier1 |

All raw lower-tier listings remain; missing optimized bodies are not treated as
zero, replaced by Tier0, or borrowed from old hosted evidence. The complete
mechanism gate is **Failed**, despite both processes completing successfully.

## Partial intercepted-body observation (not complete-gate acceptance)

At release-owner request, inspect the partial Tier1 body without promoting it to
an accepted whole comparison. Baseline IG13/0x0181 testsr15d; true reads Current
inIG14, IG15 jumps toIG65/0x0842 and retests the same r15d before yieldIG66.
R1 IG13/0x018A testsr15d; false goes to cleanupIG85, true reads CurrentIG14,
IG15 jumps toIG64/0x084D which yields without the duplicate test. The long jump
still exists. This is a narrow observed branch-shape change only.

Do not selectively report this as reduced total cost: the captured intercepted
body grew4703→5645B and main stack reservation288→312B. The dynamic iterator source is unchanged, but its body in the rebuilt Client
assembly also changed2345→3298B; adaptivePGO/inlining differs.
Both intercepted listings say SynthesizedPGO; inlinee statistics differ. No
causal attribution of frame/body or RPC tail behavior follows from this pair.

## Environment diagnosis and bounded next action

Private runtime, source/probe bytes, count, pauses and affinity requirements match;
this is not a wrong-runtime or omitted-call failure. Raw logs really lack optimized
Runtime/Server targets: not a target parser silently relabeling Tier0. Host-only
process wall diagnostics were2.77/2.92s, compared with original hosted preflight
4.03/3.67s; these include setup/JIT/pauses and are **not performance samples**.
Original hosted ubuntu24.04 reported AMD EPYC7763/sysfs; cloud is a different
virtual environment. Different scheduling/promotion opportunity is a hypothesis,
not proven cause; missing sysfs itself does not demonstrate JIT inhibition.

Stop unchanged local retries. A separately reviewed same-plan ubuntu24.04 workflow
can test that environment hypothesis once, retaining this failure. It must still
require all four Tier1 targets; no new warmup, timing or acceptance threshold.
See `physical-ownership-r1-native-plan.md` for the PR-triggered workflow freeze.

## Correctness evidence and unfilled coverage

Raw archived TUnit reports were re-read: existing exact R1 has67Unit/5Integration
passed, no failure/skip/cancel/timeout. These are macOS27.0.1/.NET10.0.11 results,
not new Linux10.0.13 tests. Tests cover Current-carrier transfer, disposal exception
precedence, pending deadline, early disposal, retained iterator/module/codec and
ownership cycles. Report87f76fb changes documentation only, so their source
applicability is preserved. Explicit successful-empty/one-item Current-read-count
assertions were not found and must not be claimed already proved. If R1 proceeds,
add narrowly scoped test-only counterexamples before product acceptance.

## Evidence

Workspace root: `/workspace/scratch/9e5903e2fbfb/sharplink-loop-exit-20261028`.
Everything retained under `artifacts/attempts/`, with001/002/003 logs and exit files.
Attempt003 includes plan, SDK/runtime/CPU/OS, build log/command, both exact prepared
snapshots, composition manifest, raw proof/host/log/native, extracted intercepted
Tier1 bodies, failed gate and `diagnostic-summary.json`. Runtime provenance is
`artifacts/runtime-install/`; reused test JSON is `artifacts/reused-r1-correctness/`.
The supplemental diagnostic summary does not modify raw evidence or gate status.
