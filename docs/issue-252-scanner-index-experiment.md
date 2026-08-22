# Issue #252 scanner-owned deadline index experiment

This branch is evidence-only. It commits no production `src/` change; the workflow applies the candidate to an isolated `dev` worktree so the control and candidate use the same benchmark source and runner.

## Hypothesis

The previous deadline-page designs made registration maintain page metadata. This prototype reverses ownership: only the timer scanner writes the page index.

After a full scan, the scanner records pages containing future deadlines. The existing `_approximateEarliestDeadline` registration update is reinterpreted as the earliest deadline registered after that full rebuild. While that unindexed earliest deadline is still in the future, timer callbacks can scan only the scanner-owned pages. When an unindexed registration becomes due, the callback performs a full scan and rebuilds the page index.

Consequences intended by the design:

- no second per-deadline marker read/write on registration;
- no completion-side page bookkeeping;
- no-deadline calls remain outside deadline scheduling;
- a registration published during a full scan is either observed by that scan or updates the existing earliest field after publication;
- page metadata is scanner-only, so there is no clear-vs-register bitmap race;
- the bitmap is tiny (32 bytes of payload at capacity 65,536; 512 bytes at the 1,048,576 hard maximum) and is allocated only by the first real deadline scan;
- a new deadline can force a full rebuild, so the optimization is deliberately conservative rather than risking a missed page.

## Predeclared gates

The combined workload runs the schema-v2 benchmark from #272 unchanged on current `dev` and the candidate, alternating three fresh-process rounds on the same hosted runner. Before seeing candidate numbers, the evidence script declares:

- overall median CPU delta across scenarios must be at least a 5% improvement;
- no scenario may regress CPU or QPS by more than 3%;
- allocation may not increase by more than 1 B/op;
- p95/p99 must remain within `base + max(0.05 ms, 25%)`.

Only if that gate passes does it run the retained registration/completion guardrail. That gate starts every 256-call batch from a fresh scheduler epoch and requires the historical <=3% median / >=2-of-3 rounds / 0-B result for both single-thread and four-worker same-page contention.

If either gate fails, this prototype is No-Go; the workload/gates are not adjusted to rescue it.
