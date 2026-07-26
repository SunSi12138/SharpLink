# SharpLink 0.8.12 Performance Validation

Chinese: [`../performance-0.8.12.md`](../performance-0.8.12.md)

Apple M4 / .NET 10.0.2 / BenchmarkDotNet 0.15.8 used one launch, five warmups, and fifteen measurements with alternating normal Build/Dispose runs. Direct Client measured 614.0/615.7 ns on the baseline and 614.8/622.2 ns on the final candidate. Dynamic Client measured 812.0/812.1 ns versus 817.4/811.2 ns; direction changed between processes and allocations stayed exactly 6.37/7.38 KB, so there is no stable regression signal. Server measured 1386.2/1383.1 ns versus 1385.4/1374.4 ns, with allocation decreasing from 12.94 to 12.88 KB. An initial branch-local Client exception boundary measured 619.2/828.4 ns and was rejected in favor of the outer no-inline cold rollback. Raw reports are under `artifacts/performance/0.8.12-builder-ab/`.
