# Producer-reservation cost audit: source 8915e61e

## Result

The final correction removes the redundant registration notification and retains the **existing** admission reservation through outgoing-producer disposal. It introduces no new owner/session field, reservation counter, per-item accounting, or catalog/version/maintenance lookup on these ordinary paths. This is **not machine-code neutrality**: the ordinary unary and streaming costs below are real and remain part of the correction.

- Local measurement baseline: `038ce547e93ee4003a38079d816df334fddc1fe0`, production tree `c242f33f0041439b176c43460864a888237b79d9`.
- Local measurement candidate: `8af020df361c5b07f4a225c50fdde95833c762ed`, production tree `8915e61e5534f6e47a5d679bfbea5c0dcb812eaa`.
- Public reproduction uses baseline `c60393508cecd20886e798a474f189f4faf52c62`, with the same c242 production tree, and the exact published candidate head with tree 8915. Five production files differ. SDK 10.0.102, Release, Linux x64; the same built assemblies ran on official runtimes 10.0.2 and 10.0.13. Selected SDK, Roslyn, MSBuild, host, CoreLib, CoreCLR, JIT and host-policy hashes are retained in the build-toolchain and per-campaign provenance files.

## Actual coverage

**40/40 final launches passed**, not 40 timing samples:

- 24 maintenance-enabled TLS/auth launches, each with the same two physical connections/sessions, one compatible update and held proxy, 30,000 unary calls, and 600 calls each for client-streaming, server-streaming, duplex, one-way client-streaming and health. Both source arms perform the update. Every launch observed all 600 one-way service completions.
- 16 retry-enabled TLS/auth launches, each with the same pool two and 30,000 successful idempotent unary calls. Maintenance is disabled in this separate, intentionally incompatible policy configuration. This exercises the ordinary retry-attempt path, not retry-failure behavior.
- Four modes: forced FullOpts, retained Tier0, non-PGO Tier1, and **tiered PGO with accelerated promotion** (recorded delay 0 / threshold 30), on both runtimes. Complete listings cover all 120 requested method/mode/tier pairs. Original broad-capture interleavings remain retained; eight narrow paired recaptures have no interleaved/missing body footers.

These probes do not record elapsed time. Functional counts, native work and managed allocation are different kinds of evidence. Earlier c242 elapsed measurements are not silently rebound to this candidate.

## Layout and IL

Nine audited ordinary object layouts, including every recorded field offset and allocation size, match: negotiated options 64 B; protocol state 40 B; session 312 B; client connection 152 B; client 560 B; pending table 104 B; admission kernel 240 B; server connection 152 B; server 480 B. Ten selection/admission methods have identical resolved IL and local signatures, including both ordinary GetReadyConnection overloads, expansion/reconnect and cluster selection variants.

The final client-stream state has 17 fields in both arms. Its comparative int/one-reference-writer instantiation is 256 B in both. For all nine actual generated request/writer combinations, state size is **272 B** and directly allocated runtime AsyncStateMachineBox type size is **344 B** in both. Awaiter/lease field ordering changes; this is not a claim that every state field offset is unchanged, or that an entire RPC allocates only 344 B.

Superseded e2f4 added a hoisted Boolean: the comparative state grew 256→264 B, but the actual generated state/box already stayed 272/344 B. Lexical follow-up 065c removes that field. Both observations are retained rather than presenting a universal 8 B allocation reduction.

## Ordinary call and entry work

The removed interface notification is absent from final source/metadata, not merely unobserved in native output. FullOpts/non-PGO Tier1 registration loses three instructions (owner load, interface-cell load and indirect call), and the old callback's tail jump disappears. Unary success gains a saved-connection load and direct call to the unchanged reservation-release implementation. Thus this particular route replaces four instructions with two; it does **not** add another decrement/CAS. The load location and call topology change, so this is not a throughput theorem. The earlier e2f4 empty-callback variant really retained an extra call route and is superseded, not relabelled neutral.

Native entry differences below occurred on both runtimes. “Reserve” is `sub rsp`, excluding unchanged pushes. Zero-store counts expand recognized initialization loops and include the zero-valued post-loop store.

| Target / mode | Reserve B, baseline→candidate | Executed zero stores | Bytes written by zero stores | Distinct zeroed bytes |
|---|---:|---:|---:|---:|
| Client-stream MoveNext, FullOpts / non-PGO Tier1 | 296→296 | 1→2 | 64→128 | 64→112 |
| Client-stream MoveNext, Tier0 / Instrumented Tier0 | 752→816 | 41→45 | 648→704 | 648→704 |
| Duplex MoveNext, Tier0 / Instrumented Tier0 | 704→720 | 37→38 | 584→600 | 584→600 |
| Registration, Instrumented Tier0 | 176→160 | 2→3 | 112→56 | 112→56 |

Client-stream optimized entry adds one 64-byte vector zero store, partly overlapping the original range: 64 extra bytes written, 48 newly covered bytes. Tier0 adds four executed zero stores and 56 written bytes. Duplex adds one 16-byte zero store. These are executed ordinary entry instructions, not just unused stack reservations. Both client-stream versions have 19 IL locals, reordered; no additional declared local explains the result by itself. The lexical return/await/EH representation changes JIT temporaries and liveness.

The profiled Tier1 samples additionally include:

- Unary frame 376→392 B and retry-attempt frame 456→472 B, with unchanged entry zeroing.
- Client-stream frame 376→392 B, four zero stores in both, written/covered bytes 208→224.
- Duplex frame 296→312 B with the same three zero stores / 160 zeroed bytes.
- Generated producer helper frame 32→48 B with unchanged zeroing.
- Server-stream frame stays 320 B but zeroing changes from 192 B in five stores to 200 B in four stores.
- PrepareGeneratedServerStreamAsync profile-dependent reservations differ among captures. All versions remain in native-coverage.json; no single favorable version is substituted for the range.

The unary and retry frame changes are reproducible in both runtime campaigns; unary repeats in the narrow captures. In accelerated PGO, the existing release/CAS is inlined in the larger caller rather than the smaller, type-guarded OnRegistered path. Unary’s observed-CAS-value stack spill/reload already exists in baseline OnRegistered, but moves to the caller; the candidate also reloads its saved connection around the inlined release where the old small method kept the owner in a register. Retry’s candidate keeps that observed CAS value in a register instead of the old callback spill. These are real ordinary-path stack/load-placement tradeoffs, not only streaming overhead. The additional 16-byte reservations do not by themselves identify new control metadata, and unchanged entry zeroing does not imply identical body instructions.

Health, one-way streaming and retry-state-machine entries have their own complete listings. Native size alone is never used to infer throughput. The final correction is not described as “no added ordinary instructions” or “zero total runtime cost.”

## Measured allocation, including unfavorable samples

The shape probe also records GC.GetTotalAllocatedBytes(precise:true) around each block of 100 calls. Six blocks per shape per process are retained, including initial and post-update blocks. These are **whole-process managed-byte windows**, not isolated invoker allocation. One-way server work can overlap the following health window; the two buckets must not be interpreted as clean per-shape attribution.

Examples from the first complete shape matrix, median bytes per 100 calls (baseline→candidate):

| Runtime / mode | Client stream | Duplex |
|---|---:|---:|
| 10.0.2 FullOpts | 384,936→381,520 | 419,660→422,256 |
| 10.0.2 accelerated PGO | 399,804→381,180 | 421,228→419,516 |
| 10.0.13 FullOpts | 395,004→408,092 | 453,424→420,128 |
| 10.0.13 accelerated PGO | 417,240→379,736 | 419,132→422,636 |

For example, the unfavorable 10.0.13 FullOpts client-stream ranges were 369,416–482,056 B versus 376,792–533,456 B. These broad, partly overlapping process ranges do not establish equivalence, regression magnitude, or per-call allocator neutrality. All 120 final allocation groups, raw six-block samples and recapture results remain in FINAL-SUMMARY.json. No threshold or budget was selected after observing them.

## Historical local evidence map and limits

The paths below identify retained local measurements, not committed repository artifacts. Fresh hosted runs generate separate exact-head artifacts as described in README.md; their results must not be substituted with these local samples.

- FINAL-SUMMARY.json: exact source/binary/runtime checks, successful cohort checks, layout/IL gates, all allocation windows.
- native-coverage.json: 120 paired target/mode/tier rows, retaining all complete observed versions.
- native-versions.json: all captured target versions, including superseded source and incomplete/interleaved native fragments with explicit completeness flags.
- shapes/evidence/final-shapes-runtime-* and final-narrow-runtime-*: raw enabled-owner TLS JSON, native listings, metadata and provenance.
- retry/evidence/final-retry-runtime-*: separate retry-enabled ordinary-success evidence.
- Original unary/e2f4 shape/retry campaigns and the metadata-only 065c arm remain distinct. The initial authored harness compilation error and insufficient first disassembly filter remain retained as setup diagnostics, not production failures.

This audit does not exercise the separately excluded rejection-handoff fixture, publish old archives, establish all-workload performance equivalence, or validate arbitrary custom stream-writer instantiations. It provides the requested source-bound ordinary ownership-cost evidence; correctness fixtures and acceptance decisions remain separate.
