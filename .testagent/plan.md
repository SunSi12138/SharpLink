# 0.8.19 regression-test plan

1. [x] Prove a contradictory authenticated provider result cannot establish a connection.
2. [x] Prove duplicate Client and Server interceptor continuations execute the service at most once.
3. [x] Prove a faulted tracked Client background task remains observable through logging.
4. [x] Prove Hosted Server Stop preserves caller cancellation together with later cleanup failure.
5. [x] Prove timer-range-exceeding polling remains cancellable and admission delay fails during configuration.
6. [x] Run the complete pre-fix Unit and Integration probes and record the exact failure set.
7. [x] Implement only proven fixes, review assertions/pseudo-mutations, and run performance A/B.
8. [x] Complete release gates and documentation; prepare the local 0.8.19 commit.
