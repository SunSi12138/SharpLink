# #728 cross-direction evidence audit

Snapshot: 2026-11-04. Frozen integration candidate: 8a13b12e799bb70a989eacac0c1b4cea22ab80ae, src tree bfc211191d91b5a77c6a8f0085a0c00a459c5583. This is not asserted to be main, a merged release, or a newly validated whole-product head.

## Finding

The highest-priority remaining synthesis gap is distinguishing a bounded architectural disposition from completion of every original benchmark and lifecycle matrix. Five directions have a disposition; #732 remains open. A structural evidence-ledger check makes omissions, duplicate directions and historical-to-current promotion explicit, but cannot prove the truth of prose or replace experiments. This work does not rerun stopped candidates merely to tick the original boxes.

## Six-direction synthesis

| Direction | Disposition | Measured benefit and cost | Unmeasured or limited scope |
| --- | --- | --- | --- |
| #729 OneWay | C internal plain/streaming async split adopted; B public static-entry candidate No-Go | Suspended calls about 48 B/op plain and 8 B/op streaming saved. No stable timing benefit. MoveNext IL+ 16.7%; six-shape AOT executable+0.243%, selected core native+28.5%; implementation net +29 lines; no new public/generated entry ABI in that change | 324 paired JIT samples; 324 full-RPC cases are a single sanity round, not stable E2E speedup. Other contract scales/AOT build cost not established |
| #730 payload absence | This pooled no-result ValueTask candidate No-Go; production unchanged | Direct-await low concurrency saves allocations, but legal AsTask/Preserve adds about 50/72.5/78.125 B/op at concurrency 8/32/128, reproduced via TCP; 480 samples reviewed | NativeAOT, full SharedMemory matrix and dedicated no-result pending design not done. Empty-request only JIT mechanism check. No exact public candidate SHA or quantified code-size/ABI maintenance evidence located |
| #731 stream nullability | Current receive+pump, receive-only and broader static-split round No-Go | Required-reference receive PGO about 0.44 ns/item better, Nullable<int> AOT about 0.24 ns/item worse; unchanged int control noise can exceed target gain. Pump state 224 B; dispatcher fields unchanged; three AOT images each 10,333,712 B | Nine pairs are 3 rounds×3 correlated lengths on one host. Full-RPC first case overflowed and run failed before complete measurements.8 KiB fixture correction stayed local/unpublished after cancellation; no retry |
| #732 method facts | Current 3.0 facts architecture has bounded behavior evidence; performance acceptance remains open | Historical narrow B dynamic+interceptor PGO improvement is not current 3.0 performance. Five-kind initial observer and eight held-empty-outbound lifecycle cells passed on branches with identical production src tree to 8a13 | M0 NoGo/M1 Hold not lifted; latest two-process phase trace is diagnostic, generated proxy/stub coverage incomplete. No claim all cancellation, inbound duplex, final lease/ALC, perf/codegen/AOT acceptance completed |
| #733 service lifetime | Acquisition specialization No-Go; keep fast path/branch design | Tagged Server IL+ 879 B/+0.55%; AOT total image+ 0.028%, principal binary+64 B; publish 10.80→10.81s. No distinguishable full-RPC benefit or allocation improvement; avoid shared-state/reference-identity hazards | Exact candidate commit not in public conclusion. Dynamic/collectible, independent Connection acquisition, SharedMemory/cross-machine and AOT runtime comparison not covered. Lab omits user-entry/module lease, so its gain is an optimistic estimate, not a strict mathematical upper bound |
| #734 client de-folding | Full D and general Selective C No-Go; five folded lifecycle entries remain default | Prototype D best JIT−8.94%, AOT image+ 1,059%, build+4,333%; C JIT−3–5%, AOT runtime+22.15%, image+ 13.91%. C 112 / D 8,224 entry lattice; source 1,537/88,445lines | Prototype gains are not production base/head full-RPC gains.24 × 3 real-RPC matrix anchors cost scale; no PMU i-cache claim. Earlier 30-shape experiment is separate. #751/#752 small adopted changes do not overturn No-Go |

## Sources and identity boundaries

candidateCommit=null in the ledger does not mean every historical candidate identity is unknown. The row covers current architecture and potentially multiple stopped alternatives; known historical source identities are recorded per evidence.sourceCommit. Resolved research heads: #729 6ece2f7a7aaa8b3650885124e26608653cb77684 (chosen production implementation a90c83db0c1c549e9e1352b1f8cdcc2f42df35b7), #731 5c5506107148b5d902f6bb9e7a6be5d0d8c2236f, and #734 measured b98fa48c37340514a09db5eea53b8fa3373436d3. Final #734 report/tool head 3c209916 remains distinct.

Historical numbers below were checked against published issue conclusions and linked reports. Original raw samples were not downloaded or independently re-audited in this round, and historical experiments were not rerun. Their uncertainty and scope limits remain those of their cited evidence. Current source identity checks are separate from those historical numerical claims.

- [#728 definition/current disposition list](https://github.com/SunSi12138/SharpLink/issues/728).
- [#729 conclusion](https://github.com/SunSi12138/SharpLink/issues/729#issuecomment-6084630079), [#796 implementation and costs](https://github.com/SunSi12138/SharpLink/pull/796). Source inspection at 8a13 retains InvokeOneWayPlainCoreAsync/InvokeOneWayStreamingCoreAsync. GitHub compare reports ca993a1aa89864755e10d1e35030a612b43d6069 is an ancestor of 8a13 (ahead 218, behind 0). The local shallow checkout could not establish ancestry; API independently supplied that fact. [#795](https://github.com/SunSi12138/SharpLink/pull/795) is now closed and unmerged despite older text saying retained Draft; draft=true is not open status.
- [#730 approved disposition](https://github.com/SunSi12138/SharpLink/issues/730#issuecomment-6085529107). Candidate provenance gaps remain explicit; no invented SHA.
- [#731 final issue decision](https://github.com/SunSi12138/SharpLink/issues/731), [failed/limited evidence](https://github.com/SunSi12138/SharpLink/issues/731#issuecomment-6091555833). Final closure scope controls over earlier comments suggesting future work.
- [#732 old B conclusion](https://github.com/SunSi12138/SharpLink/issues/732#issuecomment-6087119294): base ca993a1a; A No-Go/B qualified narrow Go. Neither substitutes for current 3.0 acceptance. [Observer run](https://github.com/SunSi12138/SharpLink/actions/runs/41895252553) tool/test head 2a3bb3f0302a38b1e96beb672ad0c4464f01d796; [held-stream run](https://github.com/SunSi12138/SharpLink/actions/runs/41898038816) head 8d2ff96ce8f54f8c0e6fa8ee3742bf87a36c29f7. Fresh Git tree API reads establish both src trees equal bfc21119; this does not relabel their complete head or CI as8a13. [Phase diagnostic limits](https://github.com/SunSi12138/SharpLink/blob/46bc6dc8489c6e6116b0e2ec040853f6a45f397f/eng/method-facts-phase/RESULTS.md): product M0 is 37548b7a35d50803212d2b0a637306636cc0a1d3, not 8a13; two-process budget exhausted.
- [#733 conclusion and limits](https://github.com/SunSi12138/SharpLink/issues/733#issuecomment-5826702894).
- [#734 final architecture decision](https://github.com/SunSi12138/SharpLink/issues/734#issuecomment-6072214899), [fixed experiment description](https://github.com/SunSi12138/SharpLink/blob/3c2099168caac99d1eda85cf386f9f7a06ffb9c9/doc/performance/client-call-shape-defolding.md). #749 closed unmerged.

## Current implementation characterization

The source snapshot records exact file hashes and anchors, independently of historical experiments:
- OneWay keeps a common selector and two internal async cores.
- Generator still emits RpcEmptyRequest/default empty request plumbing.
- Receive dispatcher retains a payloadNullable boolean and item null check.
- Server captures selected RpcMethodFacts and carries the same method fact object through dispatch; scalar facts include kind, stream count and cancellation support. Source shape alone does not prove runtime once-per-RPC for every path.
- ServiceRegistration still contains lifetime branches/fast paths.
- IRpcContractChannel retains five folded method-kind entry points.

These are static observations, not fresh JIT disassembly, runtime branch counts or native image measurements.

## Recommended 3.0 principles and remaining work

1. Preserve facts when doing so demonstrably simplifies ownership/state machines or avoids repeated work, but compare total code size, indirect calls, JIT and AOT costs before adoption. A smaller branch count alone is insufficient.
2. Keep the five folded lifecycle entries as the default architecture. Small independently measured internal optimizations remain possible; broad entry expansion has not justified its costs.
3. Keep registration/generation identity and lifecycle correctness separate from scalar facts. Static facts do not authorize bypassing module leases, admission, interceptors or extension semantics.
4. Do not introduce an old 2.0.3 compatibility veto. The user permits deliberate 3.0 breaks. Still document new API/generated ABI shape, entry count, migration expectations and maintenance cost; those are design costs, not an old-compatibility requirement.
5. Leave #732 performance and #728 completion open. Next executable performance work needs a reviewed same-workload/same-environment plan with exact current candidate, explicitly scoped runtime/code-size evidence and no reuse of old B gains. Do not reopen its exhausted trace budget or stopped variants implicitly.

No production code, grants, permission settings, D2 operation or blocked Cancel regression changed; no merge/release/deletion or new RPC/benchmark run. Historical failures remain part of the record. This report proposes no issue closure and does not alter remote issue decisions.
