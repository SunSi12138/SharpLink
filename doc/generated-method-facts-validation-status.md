# Complete method facts: recovery and validation status

## One current contract

Server dispatch resolves the complete immutable `RpcMethodFacts` once and carries
the same object through admission, invocation, observer/error projection and
persistent decoding. This applies to Unary, OneWay, client streaming, server
streaming and duplex streaming. The mandatory resolver and four invocation
signatures replace the old stub SPI; no old-shape adapter, optional resolver or
fabricated unary fallback is provided. Earlier generated identities must be
rejected and regenerated, not kept running through a compatibility mode.

Numeric generated API remains 5 at the 3.0 boundary, but the exact identity is
`sharplink-3.0-api5-complete-method-facts-v1`. See the
[migration contract](generated-method-facts-3.0-migration.md). Identity/API inventory
and negative rejection checks are safety/migration evidence, not a promise that
old source, binaries or wire peers remain compatible.

Facts store only host-owned scalars, not a stub, service, codec, Type, Assembly or
registration. Unknown is explicit; null/default and invalid identities are not
silently treated as known methods. Declared timeout presence and its nullable
TimeSpan value remain distinct. The representation supports all 127 client-stream
parameters without a timeout ordinal or secondary facts lookup.

## Recovered source and new counterexamples

The frozen recovery base is `3fafced96ea9c1cfc5398b5d8d5a20bb4e39c1e9`.
The full semantic source delta is recovered from
`37548b7a35d50803212d2b0a637306636cc0a1d3` relative to
`a6bd0352a931d5c2816e42778055e90efb01144b`, preserving subsequent base fixes and
current generated XML documentation. The old Draft #754 packed-shape proposal is
not silently restored as the current design.

The queued continuation test covers replacement and removal for all five shapes
(10 rows), with kind-valid facts, a changed replacement timeout, once-only lookup,
exact original facts identity, original registration and request-resource release.
The two TCP persistent-decode cases send a compressed 2 MiB request with a caller
token, block inside the actual compression provider, verify one started decode
operation and no business call, then replace/remove the registry entry. They
require the original facts object/result, zero replacement lookups/invocations,
and complete physical decode/resource release. Worker population is not assumed
to be one. Registry restoration and provider release are in cleanup paths.

These static-registry continuation probes do not replace dynamic retirement,
module-token, collectible ALC, receipt/admission or physical producer-lifetime
proofs. Existing dynamic tests remain; #775 and held ownership changes are separate
obligations. No #931/#933/#964 production prototype is imported by this port.

## Evidence retention gap and current gate

An earlier local candidate `ce8c85e10db70589ff936a4cd25dfa8453f8e473`
(tree `e4b681989155ee737f60718eb4cbbf75e7cf6577`) was reported to pass bounded
80 Unit, 28 TCP Integration and 296 Generator tests; nine-package/API/forwarder
checks; external dependency/manual-SPI probes; and internal-loader/CLR rejection
plus regeneration controls. Reported native Unit discovery was 2,239 including
five explicit probes, yielding the ordinary expectation of 2,234; the lease-race
harness adds eight controls for 2,242. No full ordinary or injected suite acceptance
was reported by those bounded counts.

**That workspace, compiled binaries, packages and raw test/report artifacts were
lost.** The reconstructed queued/persistent test source is a new candidate; it is
not labeled as the former tree or assigned its runtime receipts. Historic reported
passes are not current verifiable acceptance. The former attempts also included
preserved protocol-classifier/setup failures; missing raw artifacts must not be
replaced with fabricated clean logs or retroactive reports.

This recovery revision has not been built or tested. Durable source publication
precedes fresh validation so another workspace replacement cannot erase the only
copy. Before final integration, rebase/reconcile against the actual latest dev,
review the exact tree and workflow triggers, and retain fresh hosted source,
package, binary, per-test outcome and failed-attempt identities. Full ordinary and
integration suites, package smoke, generated dependency scan, semantic API ledger,
security, Trim/NativeAOT and applicable release checks remain open. Snapshot
matching means matching the reviewed C0-added API, not unchanged-base API or
semantic equivalence.

No measured performance benefit or regression acceptance follows from resolving
facts once. Historical M0 NoGo / M1 Hold remain unchanged; old #754 results and the
#775 static prototype are not acceptance for this source. This document does not
close #732 or #805, select a new broad optimization campaign, merge or release.

## Current integration base

This source-only candidate is ported onto actual dev `314caf4348c80e9f47ff313f2ee59aaeec4dd91b`
(tree `425c2f3da68892e5e10327ef859ab4150ab6beba`), after #969. Its dependency, template,
workflow and regression-fixture changes are retained, including the reviewed
10.0.14 dependency versions. No previous package or runtime result is relabeled
as validation of this integration candidate. Fresh exact-head hosted validation
and final source/API review remain required; no release or performance acceptance
is implied.
