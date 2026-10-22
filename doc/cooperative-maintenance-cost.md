# Cooperative-maintenance ordinary-path cost evidence

## Result

The adopted production source introduces no additional catalog/version/update queries, maintenance counters, or coordinator/CWT lookups in the audited ordinary RPC selection, request/response parsing and dispatch paths. The eight audited ordinary class layouts, their field sets, signatures and selection IL are preserved. This is **not** a claim of identical machine code, zero total runtime cost, or throughput equivalence.

The final no-ref callback correction removes the old **40-byte managed closure per healthy stream-progress helper call**, while retaining explicit background-scan and exceptional-ownership costs. Its remaining JIT stack and spill differences are disclosed below rather than inferred away from the allocation improvement.

The measured final production subtree is `c242f33f0041439b176c43460864a888237b79d9`. The baseline is the complete published #931 physical-ownership plus #932 intrinsic-send production source, reconstructed and checked by the portable source manifest. It has no queue correction or compile shim. The no-ref callback correction is included in the feature delta. Both arms were built with SDK 10.0.102 and run on official runtimes 10.0.2/10.0.13, Linux x64.

## Actual small-item stream observations

Each cell contains three fresh processes, each with two retained warmups followed by five samples: **24 launches,168 total samples,120 non-warmup samples**. Each call sends32,768 integers over actual authenticated TLS, using the same two physical sessions and generated service in both arms. Before the timed producer release, the server must have exactly one live cooperative call with a finite unexpired one-hour method deadline. Both arms use the same4,096-byte stream-credit window. The feature has populated maintenance enrollment and negotiated capability. No update occurs within this stream workload; separate ordinary-call launches verify a successful update.

Elapsed milliseconds, median [minimum, maximum] across the15 non-warmup samples per cell:

| Runtime / launch mode | Prerequisite | Final feature |
|---|---:|---:|
|10.0.2 FullOpts|37.10 [32.30,46.20]|35.29 [32.50,44.19]|
|10.0.2 accelerated-promotion PGO|37.81 [31.94,72.84]|39.02 [29.72,69.73]|
|10.0.13 FullOpts|31.90 [28.37,59.38]|29.97 [26.85,41.46]|
|10.0.13 accelerated-promotion PGO|38.06 [30.83,73.14]|35.69 [26.76,58.39]|

Corresponding **whole-process managed-allocation** medians in bytes:

| Runtime / launch mode | Prerequisite | Final feature |
|---|---:|---:|
|10.0.2 FullOpts|3,973,040|1,185,632|
|10.0.2 accelerated-promotion PGO|4,198,792|1,568,256|
|10.0.13 FullOpts|4,097,480|1,256,696|
|10.0.13 accelerated-promotion PGO|4,672,456|1,238,232|

The unfavorable10.0.2 PGO median and every outlying/warmup sample remain in the evidence. Ranges overlap substantially; fifteen samples within three processes are not fifteen independent environments. These observations neither establish equivalence nor a general improvement/regression. The measurement window is producer release after the first consumed item through caller-observed result, not full setup/teardown. Process allocation includes TLS, framework, application and background work and is not interchangeable with the direct40→0B helper result. No disassembly I/O ran during these elapsed measurements. PGO settings explicitly accelerate promotion; no claim is made that every timed stream ran entirely in one tier.

## Structural/native findings

- All eight ordinary object layouts/full field sets and four async-reader/dispatch field sets match. The service registry's ordinary `_services` snapshot offset remains16. Eight selection/expansion/reconnect IL bodies and local lists match, including the independently exercised actualpool1/max2 held-expansion context. The cold server registry grows88→104B while that ordinary snapshot offset stays fixed.
- New maintenance frames expand existing parser ranges/switches. The144-byte control message is parsed in cold NoInlining handlers using the existing payload by reference, not in ordinary reader async-state fields. Ready validation is still the existing phase path, not a new catalog or epoch query.
- Final server reader FullOpts and non-PGO Tier1 reserve808→824B. Both execute the same48 initialization stores zeroing760 distinct bytes. The extra slot is used only by an existing control ACK incomplete await, not ordinary request metadata. Every entry nevertheless reserves the extra16B. Tier0/Instrumented/observed PGO reader reservations match the baseline.
- Ordinary outbound phase validation retains112B Tier0/Instrumented reservation but initializes96→104B, using4→3 stores and11→10 static prologue instructions. This mixed code-generation change is disclosed, not labeled zero cost or an overall slowdown. No further representation trial was adopted.
- Finite-deadline healthy progress, three accelerated-PGO repetitions on each runtime: captured-helper reservation136→168B and inbound-wrapper120→216B persist. Captured-helper prologue initialization now matches the prerequisite exactly:120 distinct bytes,3 stores,16 static instructions. The ref predecessor initialized128B with4 stores. FullOpts instead reduces total captured-helper reservation including pushes136→120B and initialization64→8B.
- The healthy PGO representation is not merely unused stack: a map-register spill/reload survives around the existing timestamp call, and local finally funclets reserve24B instead of an8B push. Conversely, the old heap closure allocation, write-barrier/lease copy and heap dereferences disappear. One traced no-retry/no-expiry path has154→144 emitted instructions including local funclets,18→18 stack stores and15→18 stack loads, excluding callees. These bounded counts do not predict execution time.
- The original background deadline-scan correction adds16B reservation and one16B zero-store in Tier0 and Instrumented Tier0. FullOpts/non-PGO Tier1 normal scan prologues match; the failure-only catch funclet remains additional code. This is background correctness cost, not an ordinary RPC control lookup.
- PGO parsing/dispatch shapes varied in both directions in the earlier exact-source paired matrix, including source-identical dispatch. All versions and missing/inlined-body cases remain recorded; an absent standalone body is not treated as zero cost.

The final-source held-exception probe also confirms caller-thread allocations of 1,112B on first table/key registration, 472B with an existing table/new key, and 384B per reused-key batch, versus the unchanged prerequisite’s 168B. The steady +216B consists of a 128B observer box, 24B completion source and 64B drain task. The new-key +88B is a 48B owner plus 40B lock. The remaining first-registration cost is not fully attributed; worker-thread and native-handle allocations are excluded. Both official runtimes match.

## Exceptional ownership cost

The final source rebind on both runtimes measured caller-thread managed allocation of 1,112 bytes for the first table/key, 472 bytes for a new key in an existing table, and 384 bytes for a reused-key batch, versus 168 bytes in the prerequisite. The steady extra 216 bytes are the 128-byte observer, 24-byte completion source and 64-byte drain task; a new owner and lock add another 88 bytes. Worker-thread allocation and native handles are excluded. This is a cancellation-path correctness cost, not free work under cancellation-heavy load.

## Coverage and retained negatives

The previous73de source-bound matrix has64 network launches:48 plaintext across fresh dev, prerequisite and feature, plus16 actual TLS/auth pre/post-update launches. Its samples are retained under that exact source seal. The final three-arm prerequisite/ref/no-ref campaign adds36 direct finite-deadline helper launches and the24 stream launches above. An additional **eight final-feature TLS pre/post-update launches** pass on both runtimes/all four requested modes, directly rebinding reader/native/layout evidence to the adopted subtree; unchanged prerequisite listings are referenced, not represented as fresh paired launches. Each update keeps the same two sessions and held proxy, changing the expected result30→31.

QueueNotification's actual IL has no captured allocation before its try. The cold ScheduleNotification call and its closure/Task.Run allocation are inside the protected call range. This closes the pre-scheduling ownership hole without an extra healthy call.

Retained harness negatives: a client-wide timeout did not select a streaming-method deadline, which the state assertion rejected before measurement; the contract was corrected identically to explicit `[Timeout(3600)]`. The unbounded tiny-item smoke hit the existing4,096-element receive guard; both arms then used the same public4,096-byte credit window without changing item counts or pool cardinality. A runner initially disabled patch roll-forward and failed at host startup before any workload; the corrected runner checks the exact selected runtime. These are diagnostic setup samples, not hidden production failures or favorable-only timing selection.

## Reproduction and evidence

[`eng/maintenance-stream-cost/`](../eng/maintenance-stream-cost/) contains authored source, pinned build configuration, immutable public-baseline reconstruction, runner, summarizer and compact reference numbers. It contains no local binaries, raw environment capture or research archive. Run both selected official runtime hosts as described in its README. No arbitrary performance threshold is imposed.

The committed compact reference numbers describe the local source-bound campaign, not a previously uploaded raw archive. Fresh exact-head hosted evidence is produced by `cold-control-production.yml`: all four enabled TLS/native modes and the matched streaming observations on both runtimes. See the pull request for the actual run outcome and artifact links. Local raw outputs are not represented as publicly accessible artifacts.
