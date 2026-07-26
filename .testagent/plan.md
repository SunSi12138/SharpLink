# 0.8.5 regression-test plan

1. [x] Race client publication against host stop; prove a stopped accessor can never retain or return a client.
2. [x] Make scoped service activation and rollback cleanup fail together; prove both causes remain observable.
3. [x] Make scoped service and scope disposal fail together; prove cleanup continues and both causes remain observable.
4. [x] Reproduce fixed-client initial-pool rollback corruption and leased-invocation terminal error loss.
5. [x] Run focused regressions, complete suites, Release build, performance comparison, diff checks, documentation, and local commit gates after the five-item version threshold is met.
