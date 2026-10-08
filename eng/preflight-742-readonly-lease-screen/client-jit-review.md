# R retained client JIT review

Run https://github.com/SunSi12138/SharpLink/actions/runs/37855801421, helper `7b84cd6a60dc9f8d8e7dfa8e29dc82c4adc2d535`. Exact G2 tree `398d484fb5b8ab8adb75a74db7d577d929d2dc77`; R `df4383c7d00ee4a31c129ae42958e013f4ff5385`. R excludes failed A and V.

**PASS_CLIENT_BORROW_MECHANISM_ONLY.** The actual retained byte-array client roots preserve the owned 40-byte snapshot and replace its second whole-value outgoing copy with its address. No compensating whole-value copy occurs on the inspected R successful inner/controller admission path. There is a significant emitted-code tradeoff: R's inner sender calls controller admission where G2 inlines it. This report makes no throughput, CPU, allocation-total, or readiness claim.

Use with `g2-readonly-lease-preflight-review/run-37855801421-integrity-review.json`, which independently verifies official artifact hashes, source identities, binary bytes, safety results and capture provenance. The adjacent `selected-blocks.json` and `client-machine-proof.json` identify every selected method and exact SHA-256; copied disassembly files retain original bytes. All selected roots, inner senders and controller bodies are ordinary Tier1 with **Synthesized PGO**, not Dynamic PGO or OSR. Other tier versions remain in the raw artifacts; no body was substituted from the earlier A/V runs.

## Actual source and caller mapping

The original `GeneratedAbiStreamingEvidenceRunner` Client100x16/Client100x4096 scenarios produce `byte[]` values through the real client root and unsized codec path. Current managed-root probes instantiate `System.Byte[]`; compiled generic reference-type code is consequently the canonical `System.__Canon` root/sender, not an unrelated sized Int32 specialization. Each original diagnostic report validates the scenario and transport and has no benchmark-result interpretation here.

Both compiled client wrappers remain 630 code bytes. Actual managed state is 256 bytes in G2 and R, with identical fields; the send lease remains 40 bytes. No A awaiter reduction is present. The actual outer state machine's field interval `[state+0x80,state+0xA8)` is copied to `[rbp-0x278,rbp-0x250)` in all four retained MoveNext bodies: one 32-byte vector move and one 8-byte move. This is distinct from the preceding RpcDeadline copy.

| Actual cell | G2 root block and snapshot lines | R root block and snapshot lines | G2 outgoing copy/call | R address/call |
|---|---|---|---|---|
| TCP Client100x16 | block-037, 815–818 | block-037, 802–805 | 864–876 / 892 | 850–851 / 867 |
| SHM Client100x4096 | block-037, 885–888 | block-037, 971–974 | 934–946 / 962 | 1019–1020 / 1036 |

G2's outgoing lease consists of two reference-word moves and three `movsq` instructions, filling `[rsp+8,rsp+0x30)`. R instead executes `lea rdi,[rbp-0x278]` and stores that pointer at `[rsp+8]`. Its argument payload is therefore 32 bytes smaller, not a 40-byte reduction in root size or an asserted reduction in total stack reserve.

The snapshot occurs before the inlined outer wrapper's cancellation/deadline checks and before its optional sizing callback. In TCP R, snapshot IG112 precedes guards IG114–IG117; the sizing branch leaves at IG117, while unsized dispatch goes through IG119. SHM R has the same relationship at IG136 then IG138–IG141/IG143. Outlined deadline and sizing branch targets return to the borrowed-snapshot dispatch; they do not recopy the mutable root field. The pointer targets the owned local, never `[state+0x80]` directly. Both outer wrapper signatures remain by value, as independently checked from source and actual metadata.

## Callee and controller path

R TCP inner sender is block-025; R SHM inner sender is block-031. Both are 5,194 code bytes and retain the incoming snapshot pointer in `[rbp+0x18]`. This stack position is the callee view of the pointer placed at caller `[rsp+8]`.

In the R TCP inner sender, line 842 loads that pointer into RSI and line 844 calls (SHM lines 834 and 836 respectively) `StreamFlowController.TryAcquireSendCredit(byref,int)` with it. The pointer is not first expanded into a copied lease. This happens after serialization and the original post-serialization rejection observations.

The actual R TCP controller is block-027 (713 bytes); SHM is block-033 (709 bytes). It saves the pointer in R14, takes the original controller gate, and directly reads:
- State at `[r14+8]`, followed by the original state-type/null checks
- Owner at `[r14]`, compared with this controller
- Attached in the captured state
- Captured generation at `[r14+0x10]`, compared with the state's generation

Then it preserves abort/completion/FIFO/credit checks, stream and connection debits, and gate exit. No whole-value copy or new normal-path allocation site appears in this inspected admission path. The ordinary exceptional diagnostic allocation paths remain; they are not silently counted as fast-path allocations.

**Inlining is different.** G2's matched inner senders include the controller admission/validation code directly. R invokes the separately compiled controller method. Its 64-byte stack reservation and 40 bytes of local zeroing now execute at that call boundary. This is a real tradeoff, not evidence that smaller argument transport reduces whole-RPC cost.

## Deferred ownership and refund

The R inner sender retains the original source ownership boundary:
- Slow branch: lines 1315–1328 read the snapshot pointer and copy all 40 bytes into the by-value outgoing lease at `[rsp+0x48,rsp+0x70)`; line 1337 calls `AwaitPreCreditBudgetAndRetainedFlowCreditAsync` with a value lease.
- Acquired-credit failure handler: lines 1483–1496 copy all 40 bytes from that same pointer into the refund call's value argument; line 1501 calls `ReturnUnsentStreamCredit`.

These cold copies are intentional. They preserve identity beyond the synchronous borrow and through exception cleanup, and are not a compensating unconditional fast-path copy. The deferred starter and refund destination bodies were not JIT-emitted in either client diagnostic capture; this review does **not** claim to have inspected those absent JIT bodies. Same-source C# by-value capture, 14 paired ownership tests, and the separately inspected actual NativeAOT deferred starter/refund establish the broader ownership evidence. Native compiled code is not relabeled as JIT code.

Sized and first-binding source paths are unchanged. The R metadata still has by-value sized/deferred/refund parameters and the same first-binding/awaiter fields. Root code generation can differ elsewhere because compiler decisions differ; this is not a claim of byte-identical first-binding machine code.

## Recorded compiled shape, not performance

| Cell/method | G2 code bytes | R code bytes | G2 stack reserve | R stack reserve | G2 zeroed locals | R zeroed locals |
|---|---:|---:|---:|---:|---:|---:|
| TCP client MoveNext | 7946 | 8094 | 920 | 920 | 600 | 672 |
| SHM client MoveNext | 8215 | 8237 | 936 | 920 | 680 | 672 |
| TCP inner sender | 5561 | 5194 | 440 | 440 | 240 | 240 |
| SHM inner sender | 5549 | 5194 | 440 | 440 | 240 | 240 |

Zeroing values are counted from actual stores/loop bounds, not inferred from stack subtraction. Root zeroing occurs on MoveNext entry/resumption and must not be multiplied by item count. Inner sender/controller entry counts and branch/profile frequencies are not measured here. The ordinary root wrappers retain the same managed 256-byte state payload.

The intended outgoing-copy mechanism is established, while whole-method inlining/code-size/initialization tradeoffs prevent predicting a win. Any next timing screen must preserve the fixed original D/G2/R workload, every negative control and all raw pairs. A favorable bounded result would still require original full JIT and NativeAOT acceptance before promotion.
