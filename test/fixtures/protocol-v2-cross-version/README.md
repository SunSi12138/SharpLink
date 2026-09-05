# Protocol v2 package-process fixture

This fixture validates that the locally packed SharpLink 2.0 Client and Server interoperate as
separate processes across all five RPC call shapes exercised by the fixture. It intentionally does
not build or validate pre-2.0 SharpLink packages: the call-control/TimeBudget refactor lands only in
2.0, so compatibility with 1.x protocol minors is not a release requirement.

The protocol-minor floor is covered separately by focused handshake tests that prove a 2.0 peer does
not accept a pre-TimeBudget minor as if it had the new wire semantics.

Run `eng/verify-protocol-v2-cross-version.sh` after packing 2.0.0 into `artifacts/nuget`.
