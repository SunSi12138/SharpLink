# Protocol v2 current-package process fixture

This fixture validates that Client and Server from the same locally packed SharpLink version
interoperate as separate processes across Unary, Client Streaming, Server Streaming, Duplex
Streaming and OneWay. The client explicitly prepares one typed proxy after connecting and uses
it for all five shapes. The historical directory, project and script names are retained for callers;
this is not a cross-version matrix and does not establish 2.0.3 ↔ 3.0 compatibility.

SharpLink 3.0 uses Generated API 5 with the exact ABI identity
`sharplink-3.0-api5-prepared-contract-v2`, independently of Protocol v2 minor and floor 6.
The release boundary requires current-package interoperability
and correct rejection of incompatible inputs, not successful cross-major interoperability.
Protocol negotiation, ABI admission and contract-hash rejection have separate focused tests.
The TCP legacy-minor test sends normal minor 3, 4, and 5 handshake offers and checks for
`Unimplemented` and connection termination without authentication. Its current-minor control uses
a generated proxy and checks that only explicit preparation allows the business call.

Run `eng/verify-protocol-v2-cross-version.sh` after packing the current `VersionPrefix` into
`artifacts/nuget`. A successful run is evidence only for those exact current packages. This README
states the intended coverage, not the test status of any release candidate.
