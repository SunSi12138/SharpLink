# Actual generated-server JIT review: G2 → R

Run: https://github.com/SunSi12138/SharpLink/actions/runs/37855801421

## Result and boundaries

**The actual retained Server100x16 caller removes the intended second 40-byte lease copy.** R retains the first owned snapshot before the sizing callback, passes its address to the inner synchronous sender, and the actual called admission controller consumes that address directly. No unconditional compensating whole-lease copy was found in the reviewed caller/callee chain. Deferred and refund callsites retain their owned-value copies.

Important qualification: the captured G2 inner sender inlines admission/validation, whereas R calls `StreamFlowController.TryAcquireSendCredit(byref,int)`. Static code-size/frame differences do not establish a CPU, throughput, latency, or allocation benefit. Both profiles are labeled Synthesized PGO; their called counts and other inlining choices differ. The source change cannot be assigned sole causality for every emitted difference.

This is a positive, bounded server copy-mechanism finding, not overall preflight acceptance. Whole-run official artifact integrity, the safety gates, the client paths, and NativeAOT inspection are separate reviews. The present inspection independently verifies the critical reconstructed source hashes, exact nested JIT binary/generated-source maps, captured method extraction, and stable before/after source/binary maps. It performs no source edits, builds, C# execution, workload launches, timing runs, or publication.

Neither server capture emits the deferred helper's starter/MoveNext or the refund callee JIT bodies. Their outbound owned copies are visible in the real inner sender. Source and recorded binary signature/layout evidence support their unchanged value ownership; NativeAOT's actual helper bodies can corroborate this separately. No uncaptured JIT body is claimed inspected.

## Identities and exercised path

- G2 tree: `398d484fb5b8ab8adb75a74db7d577d929d2dc77`; production src: `20ee26e0cd62027012a127e073fb565d43c6a1ec`.
- R tree: `df4383c7d00ee4a31c129ae42958e013f4ff5385`; production src: `17665c929a76a00b4d6a198f5f6794974babeb68`.
- G2 Runtime DLL SHA-256: `ab7b7afafdee844217260e591e3fbbd09896336708d20280b9769ca21277583b`.
- R Runtime DLL SHA-256: `cea913526060ed6ff79295f355be73d2681d11bc2d7f3afb958df283b985b2d9`.
- G2 Benchmarks DLL SHA-256: `e3c6d8cffa2416923120bbd8daa7945b15a1181a2cf67b981c5248a15bd91646`.
- R Benchmarks DLL SHA-256: `d1656898e48da1b655bf51bed6bb1ff20d334458bb8d93c2599c1ea55f07253a`.

All 67 file members of each `rpc-output.tar.gz` and all 24 file members of each `generated-source.tar.gz` were independently rehashed and matched their complete manifests exactly. Binary maps also match both before-capture and after-capture maps. Production source maps match before-build, after-build, before-capture and after-capture. Critical source files were locally hashed against frozen maps. Exact archive, source, binary, raw capture, and method hashes are in `server-jit-machine-proof.json`; `build-proof.py` reproduces these checks read-only.

Captured runtime: .NET 10.0.12, SDK 10.0.112, Unix X64 generic VEX; ReadyToRun disabled, tiered compilation and tiered PGO enabled. Both `Server100x16` diagnostic invocations completed with zero validation failures. Diagnostic timing/allocation fields are deliberately excluded from findings.

The real source route is:

1. `GeneratedAbiStreamingEvidenceRunner.cs:309–341,352–362,377–387` selects SHM, `ServerStreaming`, 100 items of 16 bytes and calls `rpc.DownloadPayloadsAsync`.
2. Generated benchmark stub `...IBenchmarkRpc..._Stub.g.cs:74,313–345` obtains `IRpcCodec<byte[]>` and passes the service's byte-array stream to the generated server bridge. The generated source member hash matches both arms.
3. `RpcCodecProvider.cs:622,645–650` registers `BlitArrayCodec<byte>`; `StructCodec.cs:33–67` implements `IRpcCodec<T[]?>`, not `IRpcSizedCodec<T[]>`. Therefore the byte-array route is unsized. The canonical callee contains the actual array-length/payload serialization expansion and interface fallback, not a toy struct-copy wrapper.
4. `RpcSession.GeneratedServerBridge.cs:90–133` first binds the lease through `SendGeneratedStreamChunkAsync`, then sends later items through `SendGeneratedStreamChunkResolvedAsync`. The compiled retained root checks the bound flag at R block-034 line 789 / offset `0xAF6` before this path.

`Server1x16` executes only the first-binding path. Its root contains a cold retained callsite but no resolved wrapper/inner sender body was compiled in that capture. That cold code is not evidence of retained-path execution.

## Exact snapshot chain, including callback ordering

Line numbers below are within the named original `methods/block-NNN.txt`; adjacent annotated copies also give original aggregate-capture line numbers. Offsets are reconstructed directly from captured instruction bytes and block offset labels.

### G2 Server100 root, block-034

`PumpGeneratedOutboundStreamAsync<__Canon>.MoveNext`, Tier1, **Synthesized PGO**, no OSR; called count 12042. SHA-256 `6e3a49a40058852d68b1e9ea1a00feda831a31d3ed7c49a16b3ed619e1a11d88`.

- Lines 930–934, offsets `0xCF3–0xD11`: load the root's lease at `[rdx+0xA0]` (32 bytes) and `[rdx+0xC0]` (8 bytes), storing owned snapshot `[rbp-0x3C0..-0x399]`.
- Lines 942–943 branch on the exact-size codec only **after** that snapshot. The sizing callback occurs at line 2351 / `0x207E`. If it returns false, lines 2361–2364 rejoin the unsized route without rereading the root lease.
- Lines 955–967, `0xD58–0xD88`: copy the snapshot to `[rsp+0x08..+0x2F]` using two reference-sized transfers and three `movsq` instructions.
- Inner call: line 984 / `0xDDE` (aggregate line 10901). The separate 40-byte deadline occupies `[rsp+0x30..+0x57]`; it is not the lease.

### R Server100 root, block-034

Same method, Tier1, **Synthesized PGO**, no OSR; called count 10590. SHA-256 `95bc1bac143ef668acb3e5bdd6f6320295a382bb16513415e8250e2fcf8c1731`.

- Lines 822–826, `0xB7A–0xB98`: the same 32+8-byte root lease load now stores owned snapshot `[rbp-0x3B8..-0x391]`.
- Lines 834–835 branch on exact-size codec after snapshot creation. The callback is line 2159 / `0x1E3C`; its false branch at line 2172 / `0x1E65` returns to the unsized route with the snapshot intact. The callback receives addresses of item/size/codec-snapshot locals, not the lease local.
- Lines 846–847, `0xBD8–0xBDF`: `lea rdi,[rbp-0x3B8]`, then `mov [rsp+0x08],rdi`. This is the snapshot address, **not** `[root+0xA0]`.
- Inner call: line 863 / `0xC31` (aggregate line 10603). The deadline now occupies `[rsp+0x10..+0x37]`; its unchanged value copies are distinct from the removed lease copy.

All five lease fields remain owned by the snapshot: Owner at +0, State +8, Generation +16, RequestId +24, StreamId +32, then six bytes of tail padding. The first-binding controller constructor stores in R block-038 lines 134–143 independently substantiate these offsets: reference assignments at +0/+8, captured state generation +16, original request +24 and stream +32. Copying 32+8 bytes preserves the entire tuple, including the non-reference identity fields.

The separately emitted resolved wrapper agrees: G2 block-021, Tier1/**Dynamic PGO**, makes the second copy at lines 47–59; R block-027, Tier1/**Dynamic PGO**, passes `lea [rbp+0x50]` at lines 46–47. Here `[rbp+0x50]` is the wrapper's incoming by-value lease, not a mutable producer field. Both sizing callbacks occur only after wrapper entry; false sizing returns to the same owned input. These Dynamic-PGO wrapper bodies are supporting evidence, not relabeled as the Synthesized-PGO root.

## Actual inner consumption and the changed inlining shape

G2 block-022 is Tier1/**Synthesized PGO**, no OSR, called count 78720. Its lease value resides at incoming `[rbp+0x18..+0x3F]`. Admission is expanded in the inner sender: lock at line 800 / `0xA83`, State at 808 / `0xAA3`, Owner at 824 / `0xABF`, captured Generation at 831 / `0xADC`, followed by state/connection reservation under the lock.

R block-028 is Tier1/**Synthesized PGO**, no OSR, called count 38512. Its incoming `[rbp+0x18]` holds the pointer supplied above. Complete inspection finds only three uses of that slot: lines **795,1134,1302**. They correspond exactly to admission, slow handoff, and exception refund. There is no entry/fast-path whole-lease snapshot materialization:

- Line 795 / `0xA68` loads that pointer into `rsi`.
- Line 797 / `0xA6F` calls the actual `StreamFlowController.TryAcquireSendCredit(byref,int)`.
- The actual called R controller is block-030, Tier1/**Synthesized PGO**, no OSR, called count 44920. It preserves `rsi` as `r14` at line 24 / `0x24`, locks at line 39 / `0x4D`, and reads the incoming snapshot directly: State at line 47 / `0x67`, Owner at line 63 / `0x83`, Attached at line 67 / `0x95`, captured Generation at line 70 / `0xA3`. State type, terminal/abort/completed and credit checks remain. Stream/connection debit stores are lines 104/106.
- No whole-copy is introduced in that controller. Its local clear stores are unrelated temporaries. RequestId/StreamId are not needed by the uncontended admission success path; they remain in the owned tuple for later keyed work. They are not refreshed from root fields.

Tier0 and Instrumented Tier0 inner bodies also forward the incoming pointer to `TryAcquireResolvedStreamSendCredit`, and materialize whole values only at the deferred and refund handoffs. The snapshot is already complete before any inner deadline/serializer callback. There is no new box or allocation to hold it, and the recorded managed root field lists are unchanged.

## Deferred, refund, and sized ownership

R inner slow branch starts at line 1107 / `0xE98`, reached when actual admission returns false. Budget acquisition is line 1117 / `0xEBC`; `ownsWriter=false` precedes handoff at line 1121 / `0xECC`. Lines 1134–1147 / `0xF09–0xF39` dereference the borrowed snapshot and copy all 40 bytes to outgoing `[rsp+0x48..+0x6F]`; the by-value deferred helper call is line 1156 / `0xF63`. G2 likewise copies its incoming owned value at lines 1261–1273 and calls the same value-parameter helper at line 1282 / `0x109D`.

R's exception funclet first tests `creditAcquired` at line 1300 / `0x10F5`. Lines 1302–1315 / `0x10FB–0x1129` copy the snapshot to outgoing `[rsp..+0x27]`; line 1320 / `0x113D` calls by-value `ReturnUnsentStreamCredit`. G2's corresponding copy and call are lines 1428–1445. These existing conditional ownership copies are intentionally preserved, not unconditional compensation.

Source `PreCreditStreaming.cs:336–381` and recorded runtime signature metadata show the deferred helper still accepts an owned value before its first await, then awaits budget, acquires retained credit, releases budget, rechecks publication, and refunds the owned original identity if required. `RpcSession.cs:198–210` and `StreamFlowController.cs:399–425` retain original-lease validation/refund behavior. **The server capture does not emit those callee bodies, so their JIT-generated state capture is not independently proven by this capture alone.**

The sized branch still makes a whole-value lease copy. R root block-034 lines 2188–2200 copies the same snapshot to `[rsp+0x18..+0x3F]`, and calls `SendStreamChunkKnownSizeResolvedAsync` at line 2217 / `0x1F32`. G2 does so at lines 2380–2392, call 2409. Neither sized branch is claimed exercised by the byte-array workload. The first-binding source is unchanged; captured first-binding helper bodies keep their original value signatures and result ownership.

## Static code, stack reservation, and clear stores

Numbers are facts about these captures, not performance predictions. `sub rsp` excludes saved-register pushes; it is not total state-machine size.

| Body | G2 → R code bytes | Initial stack reservation | Prolog zero-store coverage |
|---|---:|---:|---|
| Server100 MoveNext, Synthesized PGO | 12197 → 11196 | 1240 → 1192 bytes | 960-byte loop in both; G2 additionally clears one qword, so 968 → 960 bytes |
| Resolved wrapper, Dynamic PGO | 617 → 567 | 232 → 232 bytes | 88 → 80 bytes |
| Unsized inner, Synthesized PGO | 4839 → 4484 | 344 → 328 bytes | 136 → 136 bytes |
| Actual admission controller, Synthesized PGO | 691 → 711 | 64 → 64 bytes | Same 8+32-byte zero-store pattern |
| Pump starter, Synthesized PGO | 702 → 702 | 336 → 336 bytes | Same initial clear sequence |
| Server1 MoveNext control, Synthesized PGO | 11945 → 11961 | 1256 → 1256 bytes | Same 1088-byte zero-store coverage |

The original Server100 first-binding resolved-return helpers are also 596→596 and 262→262 bytes respectively. Source invariance does not imply identical code for all unchanged methods: profile-driven layout/register/inlining decisions may differ. The removed lease outgoing payload is 40 bytes replaced by an 8-byte pointer (32-byte argument reduction); it must not be equated with either total frame delta above.

Recorded actual Release managed layout: generated server root remains **304 bytes**, same 25 fields; the retained lease remains 40 bytes; first-binding lease awaiter remains 56 bytes; no new byref/snapshot/task field appears. Client root remains 256 bytes in the same layout evidence. These are JIT managed-layout facts, not NativeAOT layout or an allocation gate result.

## Deliverables

- `server-jit-machine-proof.json`: full method identities, exact instruction bytes/offsets and aggregate line anchors, local source/binary/archive checks, layout evidence, and narrowly named machine checks.
- `server-jit-summary.json`: compact identities and bounded findings.
- Annotated `G2-Server100x16-block-*.txt` / `R-Server100x16-block-*.txt`, plus Server1 control roots.
- `source-path-excerpts.txt`: hashed source and generated-stub anchors.
- `build-proof.py`: reproducible read-only checks; assertions are corroborating checks, not a substitute for the control-flow and ownership review above.
