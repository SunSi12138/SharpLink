# O203C1: six untimed owner observations (prepare/review only)

O203D1 attempt1 ([40747822819](https://github.com/SunSi12138/SharpLink/actions/runs/40747822819))
retains its outcome: all8cold gates rejected and all48timed slots **not attempted**. Never
rerun that campaign or relabel its failure as a performance result.

## Source hypothesis, not yet a behavioral cause claim

Both pinned products emit server-stream `Request.Cancellable` only when the original
invocation token is cancellable or a deadline exists. Static `CancellationToken.None` +
disabled timeout selects neither. Server cancellation support additionally depends on a
dynamic module; without it, the service receives the server-loop/connection token, not a
per-request invocation token. Dynamic registration contributes a module cancellation token.
The original producer awaits an infinite delay with its received token. This predicts a
static disposal may complete client ownership but leave a server producer waiting until
connection/server shutdown. Existing logs did not contain counters, so this remains a
source-supported hypothesis, not proof of which owner survived or a new dev regression.

Do not infer token ownership from `CanBeCanceled`: a server-loop token itself is cancellable.
Record equality against captured connection and per-request tokens, cancellation callbacks,
request IDs, map presence/reason, and actual client pending/logical/stream counts.

## Frozen scope

`protocol.json` is authoritative. Exactly6fresh processes, one attempt per slot:

1. static-none: official then dev — original failure control, untouched None input.
2. static-cancellable: dev then official — fresh CTS **never explicitly cancelled** for
   case4 only; this diagnostic control cannot replace static-none.
3. dynamic-none: official then dev — original dynamic control.

Each executes one initial normal DownloadAsync(1) path probe, then original cases1–4 in
order: error before/after item, explicit cancellation, early break. Original15s cleanup
predicate is unchanged. At most5business RPCs/process,30total; no warmup, performance
window, QPS/P99, automatic retry or replacement. Each process is bounded to120s. First
failure is persisted before bounded shutdown; cleanup observed only after server stop never
changes it to Passed. Every stage and failure remains raw evidence.

Same-version endpoint pairs only: official2.0.3 owns API4/Generator2.0.3, exact dev57f06f1
owns API5/Generator3.0.0. Shared fixture compiles separately against its arm's packages.
Hosted ubuntu24.04 selects the same deterministic distinct guest-core logical CPU pair for
all processes; no physical isolation claim. Local unknown topology is not substituted.

## Observation constraints

Only fixture-local counters, cancellation notification and read-only reflection snapshots
are added. Product source/runtime assemblies are unmodified. Existing map `TryCapture`
returns immutable scalar projections under its stripe lock; no owner lease is acquired or
mutated. No server interceptor, transport wrapper, TCP relay, packet capture, raw socket,
sudo/setcap or tool installation is introduced. Actual wire flags and Cancel payload are
**not observed**; record that explicitly, alongside the exact-source inference and actual
invocation-token/owner observations. This limitation does not block the behavior controls.

## Strict dependency correction, not a permissive exception

O203D1 dynamic processes completed all functional assertions, but its offline identity
validator incorrectly expected four framework-managed dependencies to load from snapshot
10.0.12 compile assets. Actual private ASP.NET10.0.14 identities were identical across arms.
`identity.py` freezes those four exact DLL SHA256, MVID and product versions, verified against
the retained official framework archive. Their executing paths must be precisely inside the
private ASP.NET10.0.14 installation. SharpLink products and fixture/module DLLs still must
match their own arm's snapshot path/hash/source; no arbitrary-framework or path-ignore rule.
The actual loaded dependency closure is compared across all six processes. Compile assets
are recorded as such, never described as the executing framework version.

## Preparation and launch boundary

Use the existing build-only preparer with `--fixture-purpose cleanup-diagnostic`. It uses
cached published bytes locally; a fresh hosted runner may fetch the same frozen official
URLs and verify package/DLL/Generator/source hashes. No NuGet binaries are committed.
The narrow new workflow and dormant request gate require independent exact-tool-tree PASS,
root release, a normal one-request-child push and attempt1. No launch is authorized by these
files alone. Original O203D1 raw artifacts and source branches remain unchanged.

Offline analyzer validates all six receipts, identities, topology and stage ordering without
executing RPC. Complete observed functional failures remain failures; incomplete or invalid
instrumentation remains Invalid. Neither supports performance/release acceptance.
