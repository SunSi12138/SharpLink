# SharpLink Issue #998 — Audit Packet

Status: **PENDING_INDEPENDENT_AUDIT**

This packet contains the baseline, execution ledger, source review, correctness matrix, race timelines, resource audit, failure record and the independent-audit request for the session-control study tied to #998 / #775.

## Index

| File | Contents |
|---|---|
| `01-baseline.md` | frozen dev SHA, environment, actual API/ABI/wire, source inventory |
| `02-execution-ledger.md` | G0–G7 actions, commands, exits, stop conditions, all attempts |
| `03-source-review.md` | S source diff, cut point, linearization, routing, retirement, hot-path analysis |
| `04-correctness-matrix.md` | F01–F24 × P/N/C-NEG/S/Q/H |
| `05-concurrency-timeline.md` | F05/F06/F07/F09/F10/F18 monotonic timelines and evidence gaps |
| `06-performance.md` | why performance is NOT_RUN and INCONCLUSIVE |
| `07-resource-lifecycle.md` | registration/snapshot/codec/queue/stream/CTS/ALC audit |
| `08-failures-and-limitations.md` | all environment, test-oracle and candidate failures |
| `09-final-comparison.md` | P/N/S/Q/H comparison and decision |
| `10-independent-audit-request.md` | detailed external audit instructions |
| `AUDIT-REQUEST.md` | copy-paste audit request with real data |
| `evidence/` | raw events/results/logs, diffs, discovery censuses, hashes |

## Quick result

- P baseline: C-NEG proves stale-request retargeting after Replace commit and before manifest notification.
- S: closes the narrow TCP/Replace window in the exercised core cases (including F18b held-duplex ALC retention/release and F08 start-claim vs cut), but is **NO-GO as an N0 substitute**: existing dynamic-registration tests fail, unrelated services lose their session, and the refresh frame is single-shot. Full F01–F24 is not complete.
- N: `NOT_IMPLEMENTED`.
- Q/H: `NOT_IMPLEMENTED`.
- Performance: `NOT_RUN`; no A/A or paired samples.
- Overall: `INCONCLUSIVE`; no evidence to change #775’s N0 selection.

## Reproduce

```text
dotnet restore Sharplink.slnx
dotnet build Sharplink.slnx -c Release --no-restore -v minimal
dotnet run --project eng/session-control-study/Model/Model.csproj -c Release
```

Then run the compiled TUnit executable directly (not `dotnet test`, which fails under the local named-pipe restriction) with the UIDs in `evidence/test-census.txt` / `evidence/s-test-discovery.json`.

## Source

- Branch: https://github.com/SunSi12138/SharpLink/tree/study/998-session-control
- Baseline: `2948de82fff77d66fce51ecec9a5783b6ef3cb62`
- Final S test/evidence commit: `33fb48c232e7bf3843dbb3ab34420e97ca6435ff`
- Final S product source: `525a5aed918516ac1214a91c99fbb3fbb3a746e9`

This packet is not an approval to merge any candidate.
