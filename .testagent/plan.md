# 0.8.7 regression-test plan

1. [x] Block physical transport disposal; prove concurrent ClientConnection disposers await the same cleanup.
2. [x] Fail multiple generated Adapter scopes; prove Runtime Context disposal preserves every cause.
3. [x] Reproduce Hosted Server stop convergence, connection-close multi-failure loss, and cancellation-callback stranding of pending calls.
4. [x] Complete full validation, performance and package gates, documentation, and local commit after five findings.
