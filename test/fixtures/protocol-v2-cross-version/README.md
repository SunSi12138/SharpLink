# Protocol v2 real-version package/process gate

Run `bash eng/verify-protocol-v2-cross-version.sh` after packing the candidate into
`artifacts/nuget`. Python 3 and the repository .NET SDK are required. The existing
current/current five-RPC-shape control is preserved; it is no longer reported as
cross-version coverage on its own.

The gate builds **separate package-only consumers** using:

- the locally packed candidate and its packaged Generator;
- published NuGet **2.0.3**, source commit `4f9295ffb37ce68985051c11bfd9afc0e23166f3`,
  and its own packaged Generator (never a current build renamed as 2.0.3);
- the candidate with one extra synthetic contract method, changing the assembly
  wire hash while retaining the original contract ID.

Each consumer has a fresh private NuGet cache and source configuration. Published
SharpLink packages can only come from nuget.org; candidate SharpLink packages are
source-mapped to the supplied local feed. The gate verifies resolved versions,
nuspec source commits, package bytes, the actual compiler Analyzer input, generated
API/ABI/Generator identity, and the loaded runtime DLL paths/versions/SHA256 against
those exact packages. Generated sources and MVIDs are retained as evidence.

## Expected behavior, not a new blanket compatibility promise

[Wire identity](../../../doc/rpc-wire-identity.md) requires exact `RpcAssemblyHash`
equality and rejects differing hashes with `FailedPrecondition` before a business
Request is emitted. [Protocol v2](../../../doc/protocol-v2.md) uses minor 4 in both
published 2.0.3 and the 3.0 candidate. Generated API4 versus API5 is a **process-local
ABI boundary**, not itself a reason to reject a remote peer. No ABI remapping,
manifest rewriting, or production protocol changes are used here.

For this unchanged primitive/stream contract, both matching Generators must produce
the same wire hash. An unexpected difference fails the gate for investigation; it
is **not** automatically relabeled an expected rejection. The deliberately changed
contract must produce a different hash, not a missing contract.

Nine independent server/client process pairs run serially over loopback TCP:

1. Current/current, published/published, and changed/changed positive controls.
2. Published server/current client and current server/published client positives.
3. Changed server/current client and the reverse, plus changed server/published
   client and the reverse: explicit hash-rejection negatives.

Each positive executes Unary, client stream, server stream, duplex, and OneWay.
Each negative must complete handshake/manifest bootstrap, reject `Get<T>()` with
`FailedPrecondition` naming the actual two hashes, execute zero server methods,
and emit **zero business Request frames**. A transparent byte-preserving relay
records Protocol v2 frame metadata to verify the last condition. Timeout, missing
package, compiler error, failed handshake, connection close, or generic exception
is never accepted as an expected rejection.

The narrow `protocol-v2-process-gate.yml` workflow runs on ready dev/main PRs
against the exact PR head, builds only the five-package peer closure, and retains
failure evidence. Draft PRs skip execution until independent review. The full
Release Gate also invokes this same script; running that entire workflow is not
necessary to validate a tooling-only PR.

This is a focused Linux release gate, not the full codec/transport/security/
cancellation/deadline/Trim/AOT matrix, nor a promise that every different contract
or codec can interoperate. Existing ABI-mixing and protocol-minor rejection tests
remain independent and unchanged.

## Evidence and source identity

Every invocation creates a new `artifacts/protocol-v2-cross-version/attempt-*`
directory. Nothing deletes or overwrites a previous attempt. `evidence/` contains
commands, exit codes, SDK information, package provenance, generated source,
client/server logs, observed frame metadata, and a fail-closed `report.json`.
`work/` holds isolated build/cache files and is not uploaded by the workflow.

Options: `--packages PATH`, `--output PATH`, `--dotnet PATH`, and
`--candidate-commit FULL_SHA`. By default packages must identify HEAD. A tool-only
later commit may use earlier candidate packages by specifying their exact source
SHA; the gate rejects differences in `src`, root build/dependency props, or
`global.json`. This records distinct tool and package source identities rather
than rewriting an old package result as a new product build.

Harness regression tests:

```sh
python3 -m unittest discover -s eng/tests -p test_protocol_v2_cross_version.py -v
```
