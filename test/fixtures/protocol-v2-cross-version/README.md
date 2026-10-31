# Protocol v2 current-package process fixture

This fixture validates that Client and Server from the same locally packed SharpLink version
interoperate as separate processes across Unary, Client Streaming, Server Streaming, Duplex
Streaming and OneWay. The historical directory, project and script names are retained for callers;
this is not a cross-version matrix and does not establish 2.0.3 ↔ 3.0 compatibility.

SharpLink 3.0 uses Generated API 5 with its exact owner-bound ABI identity, independently of the
Protocol v2 minor-4 wire generation. The release boundary requires current-package interoperability
and correct rejection of incompatible inputs, not successful cross-major interoperability.
Protocol negotiation, ABI admission and contract-hash rejection have separate focused tests.
The legacy-minor TCP test also sends a coalesced valid RPC, checks that it is not executed and waits
for connection termination, with a current-minor positive control for the same raw request.

Run `eng/verify-protocol-v2-cross-version.sh` after packing the current `VersionPrefix` into
`artifacts/nuget`. A successful run is evidence only for those exact current packages. This README
states the intended coverage, not the test status of any release candidate.
