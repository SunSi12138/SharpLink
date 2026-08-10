# Protocol v2 cross-version process fixture

The same source is compiled twice: once against published SharpLink 1.1.1 packages (Generated API
3) and once against the locally packed SharpLink 2.0.0 packages (Generated API 4). The validation
script starts separate client and server processes for all four combinations. Generated assemblies
are never shared across versions; only Protocol v2 frames cross the process boundary.

Run `eng/verify-protocol-v2-cross-version.sh` after packing 2.0.0 into `artifacts/nuget`.
