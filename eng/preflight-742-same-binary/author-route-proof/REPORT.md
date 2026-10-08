# Actual generated-RPC route-count proof

Result: PASS in three fresh OFF processes and three fresh ON processes, all using the same counter-only DLLs. Final Release build: zero warnings/errors. No blocked process, OS transport, security escalation, or external publication. The source patch replays exactly onto the frozen adapter tree.

## Exact measured counters (64 calls per stage)

| Stage / receiving side | RegisterCore | DispatchChunk | CompleteStream | G helper entry | G claim | G completed | Drain notification | Either request-retirement entry |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Pure unary, OFF, client and server | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| Pure unary, ON, client and server | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| Server1, OFF, client | 64 | 64 | 64 | 0 | 0 | 0 | 64 | 0 |
| Server1, ON, client | 64 | 64 | 64 | 64 | 64 | 64 | 64 | 0 |
| Server1, either mode, server | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |

The 8-call warmup stages have the same shape scaled to 8. Pure unary runs first, before any stream. Every AddAsync(7,9) returns 16; every DownloadAsync(1) yields exactly [0]. Both CompleteRequestStreams entry points, including null-map/lookup-miss/empty-drain classifications, remain exactly zero during these healthy calls. CompleteStream null-map/miss counts are zero. CompleteAll is zero throughout all call stages.

The probe uses the exact IStreamLoadService and StreamLoadService declarations copied unchanged from StreamLoadTest; the generated proxy and generated server stub are compiled normally. Their generated sources are retained in `generated/`. Client/server builders, sessions, codecs, framing, pending calls, stream routing and shutdown all execute. There is no direct StreamManager invocation in the workload.

## Frames and cleanup

Measured pure unary: server Request=64; client Response=64; DATA and StreamComplete=0.
Measured Server1: server Request=64; client StreamData=64, StreamComplete=64, Response=64 (the existing final response acknowledgement).

Each process totals 72 unary calls, 72 Server1 calls, and one separately reported post-stream unary fence. After that fence the exact totals are server Request=145, WindowUpdate=72; client Response=145, StreamData=72, StreamComplete=72. Error-flag and Cancel frame counts are zero. Handshake/contract-manifest and one startup Ping/Pong are reported separately. WindowUpdate consumption can cross warmup/measurement snapshot boundaries (some measured deltas are 65); the final unary fence establishes the exact all-streams total of 72, so no per-stage WindowUpdate equivalence is claimed.

At every workload snapshot: pending/active calls=0; active routes=0; every captured entry retired and detached with no dispatch still held; RPC reference/token fields cleared; pool has zero int dispatchers after pure unary and one reused clean dispatcher after streaming. All 72 captured entries are retired. The existing HasRetainedReferencesForTests is unsuitable for int because `_current is not null` is always true for default(int); the probe inspects the same actual reference/token fields directly without changing that test helper or shipping cleanup.

Teardown is separately snapshotted: CompleteAll=2 per side, including exactly one repeated idempotent entry per side. Server first entry has a null map; client first entry has an existing empty map. Actual drained entries=0 on both sides. No other route counter changes during teardown. Both framework supervisors report IsDrained=true, ActiveTasks=0, RetainedFailures=0. Final six stderr files are empty; earlier probe-development assertion failures remain in the worktree's root draft logs.

## Source explanation and limits

Frozen adapter source references:
- StreamLoadTest/Program.cs:555-556 invokes AddAsync(7,9); contract at 877-881 and implementation at 893-895.
- SharpLinkClient.Invokers.cs:389 classifies AddAsync as PendingCallKind.Unary.
- ClientConnection.cs:487 guards stream completion with ServerStreaming/DuplexStreaming.
- SharpLinkServer.PreAdmissionStreams.cs:49 and :94 return immediately when clientStreamCount=0.
- SharpLinkServer.InvocationDispatch.cs:553-570 healthy ReleaseDispatchResources has no StreamManager call.
- SharpLinkServer.PreAdmissionStreams.cs:210-215 does call CompleteRequestStreams for failed requests; SharpLinkServer.AdmissionDispatch.cs:793-805 also has pending-admission cleanup, and Interceptors.cs:355-370 gates dynamic stream cleanup on hasRequestStreams. These error/streaming branches explain why generic source scans can find request cleanup without putting it on the healthy unary path. No failure injection was performed here.

This is route evidence only. Interlocked counters, frame dictionaries, and retained diagnostic entry captures deliberately perturb execution. This binary must not be used for timing or allocation claims. Paired in-memory pipelines do not establish performance equivalence to TCP, UDS, shared memory, or OS pipes. The results establish that this healthy pure-unary workload does not enter the changed completion path, while the Server1 positive control does.

## Provenance and reproduction

Base adapter tree: 7a9f9f313526f926755da2fcd2119303b86f7a1f.
Diagnostic tree: f6c0f3b283ffda5fc85f104f40e6ba9d213a3b85.
Patch SHA-256: 9f2c6538d7ec7f56777401678cd7ab9bed9485d9f8c536317c491b5963aa9b81.
Runtime DLL SHA-256: eb4e2fc99ae31e1a197aeea292897d5f451942bd271841616c1e9ef8494c3452.
Probe DLL SHA-256: 14687ebcd03a9df7910279c693448bc83a786d7b28104e34498432d1c33a54b0.

`manifest.json` contains per-run hashes. `binary-sha256-before.txt` and `binary-sha256-after.txt` are identical and cover every loaded SharpLink DLL. `generated-rpc-route-count.patch` is diagnostic-only and applies after the adapter patch, never to the timed adapter. Index-only replay reconstructs the exact diagnostic tree. Activate /workspace/shared/sharplink-dev/activate.sh, then run test/SharpLink.RouteCountProbe/run-proof.sh from the diagnostic checkout. Full raw results are mode-{0,1}-run-{1,2,3}.json; counts-summary.json is a smaller exact extraction.
