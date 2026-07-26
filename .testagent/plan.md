# 0.8.6 regression-test plan

1. [x] Force unexpected writer completion failure during Stream transport disposal; prove reader and owned stream cleanup still run.
2. [x] Force unexpected pipeline completion failure during RpcSession disposal; prove transport cleanup still runs and concurrent disposers share one outcome.
3. [x] Reproduce supervised connection-service error loss, server-wide cleanup error loss, and unsupervised Hosted Server run failure.
4. [x] Run complete suites, Release build, performance comparison, package smoke, documentation, and local commit gates after five findings.
