# SharpLink 0.8.14 Performance Validation

Chinese: [`../performance-0.8.14.md`](../performance-0.8.14.md)

Apple M4 / .NET 10.0.2 built 0.8.13 commit `7e9c858` and the final candidate into separate processes. Tiered compilation was disabled identically, with two reversed-order runs and twelve measurement samples per workload.

Uncontended flow-credit acquire/update measured 21.73/22.13 ns on the baseline versus 21.58/21.84 ns on the candidate, all at 0 B/op. Normal producer pending register/complete measured 44.42/45.49 ns versus 45.46/44.81 ns; direction changed between processes and both retained 48 B/op. Short ASCII named-pipe normalization measured 138.09/148.80 ns versus 139.28/144.16 ns, again reversing direction and retaining 272 B/op. There is no stable regression signal.

The first flow candidate added about 1-2% to the normal path. The final design moves bypass scanning to a no-inline contended helper and revalidates the original stream-state identity after reacquiring the lock, restoring the hot path without allowing a completion race to recreate a closed stream. An extra candidate run after that identity check landed at 21.61/45.40/140.52 ns with the same 0/48/272 B/op, still inside the reversed A/B range. The driver and raw logs are under `artifacts/performance/0.8.14-transport-flow-ab/`.
