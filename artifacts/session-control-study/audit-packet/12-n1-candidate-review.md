# 12 — N1 Static-Bind Candidate Review

Status: **NOT_QUALIFIED / PENDING_INDEPENDENT_AUDIT**. This is a source review and pure-component check of the existing N1 static-bind checkpoint. It is **not** the N0 full candidate and must not be used for performance or as a replacement for #775's N0.

## 12.1 Source identity

| Item | Value |
|---|---|
| Repository | `C:\Users\sunsi\Desktop\SharpLink-775-N1-Implementation` (same GitHub origin) |
| N1 source HEAD | `131df413db8e222a335ad15561dd10612dece6a3` |
| Branch | `codex/775-n1-static-bind` originally; study review branch `study/998-n1-review` |
| Base merge | `2948de82fff77d66fce51ecec9a5783b6ef3cb62` |
| Study toolchain commit | `e901bcfc907831e7d9191afb8828507c20300f58` (only `global.json` 10.0.102→10.0.404) |
| Pushed branch | https://github.com/SunSi12138/SharpLink/tree/study/998-n1-review |
| Head commit | https://github.com/SunSi12138/SharpLink/commit/e901bcfc907831e7d9191afb8828507c20300f58 |
| N1 protocol | `doc/validation/issue-775-n1-static-bind-protocol.md` |
| Failure audit | `doc/validation/issue-775-n1-prepare-failure-audit.md` |

`git diff --stat 2948de82..e901bcfc` is preserved in `evidence/n1-review/diff-stat.txt`. The candidate is 131df413 plus the toolchain-only commit; no product source was changed by this review.

## 12.2 What N1 actually implements

Observed in source:

- wire 6 / floor 6; API6 exact identity `sharplink-3.0-api6-contract-channel-v1`;
- `BindContract=17`, `BindAck=18`, `BindingInvalidated=19`, `BindingRelease=20`, `RequestAccepted=21`, `OneWayInputRequest=22` are present in the wire enum; the static Unary/no-input OneWay checkpoint does not implement the full stream/OneWay-input behavior behind them;
- 32B scalar `RpcMethodDescriptor` / full method facts;
- `ContractBindRequest`/`ContractBindAck` 40B (no receipt) / 112B (receipt), acceptance token `(incarnation<<32)|slot`, 65536 binding / 1024 operation hard quotas;
- per-Session binding table, immutable method table, per-operation predecessor, queue/flush/Ack/confirmation, early Ack staging, physical protection;
- static Unary and no-input OneWay only; ClientStreaming/ServerStreaming/Duplex/input OneWay and dynamic local contracts are explicitly rejected.

It is **not** N0: no whole-group receipt publication, no dynamic Register/Replace receipt production, no ABA suite, no five-family owner/admission suite, no complete ALC/stream physical-tail matrix. The N1 checkpoint itself states this.

## 12.3 Historical hosted result (V1) — failure preserved

The only hosted run is [#43693296449](https://github.com/SunSi12138/SharpLink/actions/runs/43693296449), job `132748271127`, attempt 1, source `be24fda4`:

- 36 component/pre-connect checks passed;
- first RPC case failed at the very first `proxy.PrepareAsync(5s)` with `DeadlineExceeded`;
- no RPC case completed; the remaining 7 cases did not start;
- cleanup separately hit the 3 s `client.StopAsync` guard (source task still `WaitingForActivation`; wait promise faulted); this is `INCONCLUSIVE` for client physical cleanup, not proof of a leak;
- final server binding/operation/protected counters were 0;
- no retry, no second job, no larger timeout.

Raw evidence copied to `evidence/n1-review/V1-*` (outcome/recovery, offline consumption, result JSON, run log).

## 12.4 V2 pure components — executed locally, no RPC

Because the frozen N1 protocol says **no local RPC** and V2 has no second-hosted-run authorization, this review ran only the permitted pure component modes, with SDK 10.0.404 (not the protocol's SDK 10.0.115) on this host:

| Mode | Checks | Exit |
|---|---:|---:|
| `components` | 23 | 0 |
| `selection-components` | 4 | 0 |
| `contention-components` | 10 | 0 |
| `cluster-stop-components` | 6 | 0 |
| `diagnostic-components` | 9 | 0 |
| `publication-components` | 11 | 0 |

Total: **63 checks, all exit 0**. Logs and `n1-result.json` per mode are in `evidence/n1-review/`. This validates the pure wire/facts/quota/diagnostic/control components on this toolchain. It does **not** validate a single real RPC, Bind/Ack/flush, server admission, Codec/activation, ALC, or Stop owner.

## 12.5 What this changes

- It proves a real N-like static candidate exists in the Desktop N1 worktree and can build with the installed .NET 10 SDK after a declared toolchain-only change.
- It does **not** turn N into a qualified arm. N remains `NOT_QUALIFIED` for #998; the full F01–F24 and N-vs-S comparison still cannot run.
- The V1 hosted failure is still the authoritative functional result. V2 components do not fix or explain the first Prepare deadline; no RPC was rerun.
- No performance is allowed. N1's own cost qualification is HOLD (+112 B/request source estimate, not measured).

## 12.6 Required next step for N

1. Independently review N1 V2 source/protocol using `13-n1-v2-audit-prep.md` and authorize (or reject) a second hosted run; do not run locally. In particular, resolve whether the passive frame gate's full-payload copy / verified 513th-frame fixture throw and the broad Stop filter can distort the eight-case run before authorization.
2. If authorized, run the exact 8-case protocol with SDK 10.0.115/runtime 10.0.15 and preserve first-failure diagnostics.
3. Only after N1 passes all 8 static cases, extend to the N0 group/receipt/dynamic/five-family obligations; static N1 alone is not a substitute.
4. Then re-run the #998 correctness matrix and only then consider performance.

Until then, N is **NOT_QUALIFIED**, not PASS and not a candidate for the performance stage.
