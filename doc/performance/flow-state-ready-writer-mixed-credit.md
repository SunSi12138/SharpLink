# Mixed-size ready-writer credit contract

This increment removes the research coordinator's fixed-credit-size assumption. Every prepared DATA frame now carries its own flow-control credit byte count. The packet remains serialized once; the extra integer lives beside the prepared packet in the existing bounded per-stream ring.

The disposable SendPump hook forwards the exact credit count through OwnedFrame completion without growing OwnedFrame: its serialized length is derived from Memory.Length and the former Length backing-field footprint is reused for ready credit bytes. No per-frame completion object, delegate or marker is introduced.

Correctness is based on observable flow-control contracts, not internal implementation equivalence. Tests use the frozen StreamFlowController only as an oracle for three specified behaviors: a connection-credit-blocked older waiter is not bypassed by a younger fitting waiter; stream-local blockage may be skipped so another stream can progress; and an oversized item may borrow only when both windows are fully restored and must repay before later connection admission.

B3 exercises the same rules with 4/8/20-byte mixed DATA on one connection, verifies exact stream/connection conservation and the final zero outstanding ledger, and keeps the existing generation/lifecycle/abort checks. Fixed-size performance cases still call the default overload and therefore keep their exact population and workload.

This is a contract-preservation check. It does not require B3 to reproduce the frozen controller's internal lock, dictionary or waiter objects. #735's hard bounds, fairness behavior, oversized borrow/repay, late credit, exactly-once ownership and ABA/terminal rules remain the acceptance boundary.
