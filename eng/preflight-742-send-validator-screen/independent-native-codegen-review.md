# Independent NativeAOT send-validator review

Verdict: **PASS for the intended compiled mechanism only**. This is not a performance result or promotion decision.

Run: https://github.com/SunSi12138/SharpLink/actions/runs/37823636416  
Helper commit: `815c1ac1ec1ec4141f38cfe06814d8d0caf4b257`.

## Byte identity and scope

The actual RPC ELFs were independently hashed, re-disassembled with the installed `objdump`, and checked against the retained focused instruction text. G2 SHA-256 is `8e18ac39a35131476976372e0d73c90f9778506c135b7c25281dc098af881d59`; V is `d612d20aa0b0396fae31ecf2cb0825e5856ebf0ecbb7231a4ffeb723c554a802`. See `actual-extraction.json` and the `actual-controller-*.txt` / `actual-rpc-*.txt` excerpts. The separate verifier owns archive/source/safety provenance.

## Positive control and treatment

G2 `StreamFlowController__TryValidateSendLease` at `0x142650` calls `RhpCheckedAssignRefESI` at `0x142680`. `ValidateSendLease` passes its stack local at `0x142620`. The assignment helper stores the reference then performs heap-bound checks. This is a checked reference transport cost; it is not a lock, atomic operation, or evidence of card dirtying on that stack-local path.

V `StreamFlowController__GetValidSendState` at `0x1425f0` is an actual outlined 61-byte function. It has no calls and no object-reference output store. It returns the validated state in RAX or zero. This result was established by inspecting the emitted instructions, not by the disappearance of an old symbol.

All guards survive: state load `0x1425f4`, input null check `0x1425f8`, SendState type compare `0x142604`, owner compare `0x14260d`, non-null cast-result check `0x142612`, Attached check `0x142617`, state generation load `0x14261d`, and generation comparison `0x142621`. Success returns at `0x142627`; failure zeroes RAX at `0x142629`.

## Caller behavior

- Resolved `TryAcquireSendCredit_0` and `AcquireSendCreditAsync` still call the throwing wrapper at `0x13f761` and `0x13f887`. Their mnemonic/operand streams are identical to G2 after removing call-target address and RIP-relative relocation differences. Input validation, cancellation, lock, terminal, abort, completion and reserve behavior remain present.
- `ValidateSendLease` calls the new helper at `0x1425d4`, rejects null at `0x1425dc`, and calls the same stream-closed exception construction and throw at `0x1425e0` / `0x1425e8`.
- Contended admission still checks cancellation, acquires the gate at `0x13f9d9`, checks terminal, calls the new helper at `0x13f9fd`, and rejects null at `0x13fa08` to stream-closed at `0x13fb36`. Abort and Completed checks remain at `0x13fa13` / `0x13fa1c`. The valid expected state is stored in the waiter using the legitimate reference barrier at `0x13faed`; the captured generation is stored at `0x13fafa`. Queue admission and cancellation-aware waiting remain.
- Resolved refund calls the helper at `0x13fd81`. Null and abort branches at `0x13fd89` / `0x13fd94` go to the no-op gate-exit path at `0x13fe4b`. Positive credit validation, terminal no-op, overflow checks, duplicate-refund limits and pooled-state retirement remain. A normal compiler-generated local spill later in refund is not the removed out-parameter transport.

Legitimate reference assignment barriers in waiter construction and other state-management code remain; no whole-method or whole-binary barrier ban was applied.

## Actual RPC reachability

V client `SendClientStreamAsync<Int32>.MoveNext` calls the resolved sized sender at `0x48a510`; generated outbound pump does so at `0x49645c`. Both target `SendStreamChunkKnownSizeResolvedAsync<Int32>` at `0x64e460`, which calls its builder Start at `0x64e531`. Start (`0x67ff60`) calls the sender MoveNext at `0x67ffb5`; that MoveNext (`0x4975d0`) calls controller `AcquireSendCreditAsync` at `0x4976ee`. The latter reaches the reviewed validation chain. The canonical resolved sender MoveNext also contains the controller call at `0x5615f9`.

Thus the changed operation remains on the negotiated-flow, resolved item-admission path. Its presence or removal says nothing about how frequently a measured workload takes that path or what share of CPU time it consumes.

## Limits

No timing or profiling was performed. No throughput, latency, CPU, allocation or JIT code-generation improvement is established. Existing G2 No-Go evidence remains unchanged. Separate safety results must be combined with this mechanism result before considering an authorized performance comparison.
