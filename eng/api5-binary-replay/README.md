# Real API5 binary registration regression

This bounded Linux/.NET 10 regression uses the **unmodified historical generator**
at `d5665d057e18f9b39fe80bcc5911900eb4663ce2`, not a hand-written manifest or
Reflection.Emit substitute. It reconstructs authentic generated binaries from
historical source today; it does not claim to retain or reproduce bit-for-bit a
binary from the original historical CI.

## Contract and oracle

The old baseline has API 5, Protocol 2, and exact identity
`sharplink-3.0-api5-owner-bound-unsafe-blit-v1`. The candidate must have API 5,
Protocol 2, and `sharplink-3.0-api5-complete-method-facts-v1`. Numeric API or package
version equality is not sufficient compatibility. A future deliberate ABI change
requires explicitly reviewing this matrix and its identities, not broadening the
negative oracle to accept arbitrary failures.

Both generations pack actual Abstractions, Runtime, Client and SDK packages. Each
package/symbol archive is checked against its clean source commit. Separate local
feeds, fresh package caches, exact PackageReferences and source mapping keep the
otherwise identically versioned packages apart. The same unary Echo contract from
the pinned historical checkout is compiled by each generation's genuine packaged
generator. No generated source or binary is rewritten.

Three fresh processes exercise the public `Client.RegisterAssembly` path:

1. Old plugin / old host must register, expose exactly one real `IRpcStub`, and
   unregister with references released.
2. Candidate plugin / candidate host must satisfy the same positive control.
3. Old plugin / candidate host must return `IncompatibleManifest`, naming both
   exact ABI identities. A missing dependency, generic loader exception, restore
   failure, or malformed fixture is not a passing negative.

These are registration controls, not end-to-end RPC calls. This test covers one
unary contract and the Client manifest-loader path, not all RPC shapes, Server
registration, arbitrary previous API5 branches, wire compatibility, collectible
lifetime guarantees, full Release Gate acceptance, or approval of breaking SPI
changes. There is no cross-platform promise.

## Running and maintenance

The `API5 binary compatibility regression` workflow runs on relevant PR changes
and supports manual dispatch. It uses the actual checked-out candidate commit
(including a PR merge commit when GitHub supplies one), and records that exact
identity. It has no publish step and does not alter release triggers.

For local use, start with clean candidate and historical checkouts, .NET from
`global.json`, Python 3 and Git. With the historical commit available locally:

```bash
git worktree add --detach artifacts/api5-source-old d5665d057e18f9b39fe80bcc5911900eb4663ce2
python3 eng/api5-binary-replay/verify-real-api5-binary.py \
  artifacts/api5-source-old . artifacts/api5-replay "$(git rev-parse HEAD)"
```

Choose a new output directory for another run. Existing output is rejected rather
than mixed with stale evidence. Do not edit either checkout to make a control pass.
If the baseline must change, update the historical ref in both the script and
workflow, and review the baseline identity and generated SPI evidence together.

## Evidence

An always-run upload retains evidence for 30 days, including partial failures:
source commit/status logs, SDK/runtime information, all pack/restore/build/run
logs, package and symbol archives, original fixture and host source, project files,
NuGet configuration, resolved `project.assets.json`, generated source, and both
plugin and host build outputs (including deps/runtimeconfig files). Successful
`result.json` records exact source commits and hashes for packages, plugins, host
assemblies, fixture/host sources, project/restore inputs and resolved assets.

The evidence intentionally includes hosts so each successful registration probe
can be replayed with the matching .NET runtime. Retain the two generation folders
separately; do not copy one generation's packages or DLLs over the other. A failed
run has no success record and must be diagnosed from its preserved logs.
