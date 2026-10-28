# Result: correctness/mechanism candidate, not performance Go

Tested exact commit: **b49fe3f0e8754e0c9542027d93c77b3a675ccfa2** (documentation over product-change commit **758de8eba8ac0143070ad9fb46db4aa63e73575e**), base **f724c7aa**. Local unpublished branch `codex/research-wakeup-fenced-write-elision`.

## Completed

- Release unit-test project build: **0 warnings, 0 errors**.
- Production reference boundary guard: **PASS**, 10 projects / 15 declarations / 15 active references.
- Maintainability debt gate: **PASS**, 1,260 files; historical 17 oversized files within existing allowances.
- **88/88 focused tests passed**, no skips: Wakeup14, SendPump13, TimedWaitStop1, IdleShutdown7, ProgressIsolation14, TimeBudgetPublication3, UnobservedTask2, RuntimeFlushPolicy6, BackpressuredSendOwnership28. Both newly added tests passed.
- Bounded abstract TSO model rejects unfenced return and fence-without-reread; no lost-wake terminal for original or fenced-reread within this model.
- Separate SC model: original1,416 / candidate1,294 states; no duplicate/stale-core completion in modeled two-producer/timer/rearm interleavings.
- Actual **x64 Instrumented Tier0** Signal body shows `lock or dword ptr [rsp], 0` between first and second `_state` loads. The exact-latched return avoids the shared-word RMW but retains a full local-stack fence. Original live-arm/latch fallback remains. This is **not** an instruction-cost measurement.

Runtime: SDK10.0.102 / Microsoft.NETCore.App10.0.2 on Linux x64. Natural tiering/PGO retained; only targeted JitDisasm enabled for Wakeup tests. Test elapsed times are test-run diagnostics, not accepted performance samples.

## Preserved iterations / limitations

- Initial static guard attempt could not locate dotnet (exit2 before managed execution); corrected by using the established executor runtime. Subsequent guards passed.
- First implementation2e2c77d also passed88 tests; its592-byte Instrumented Tier0 body called Thread.MemoryBarrier as a wrapper. Raw output and binaries/hashes retained under `artifacts/attempts/001-thread-wrapper`; no evidence erased.
- Final raw logs, native listing, binaries and SHA256 manifest: `artifacts/evidence/`.
- **No Tier1 native body captured, no ARM64 emitted/executed evidence, no herd run, no timing or contended/uncontended cost comparison, no full release suite, no official2.0.3 performance comparison, no push.** The small models are not a complete language/ISA proof. Independent publication-protocol/native review remains required.

## Next decision

Review `PUBLICATION-PROOF.md`, every production caller and both consumer clear/recheck protocols. If that passes, separately approve a fixed matched O/D/C campaign with low-load and contended cases from the architecture report. Do not ship from test success, assume ARM from x64, remove the fence, or interpret local-stack fencing as free. If cost wins only under contention but hurts c1, preserve and report both rather than cherry-picking.
