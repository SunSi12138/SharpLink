# #805 frozen package assembly grants

Target: `8a13b12e799bb70a989eacac0c1b4cea22ab80ae`, source tree `bfc211191d91b5a77c6a8f0085a0c00a459c5583`. This is the observed current `wip/775-code-recovery-20261102` head, not a release approval. `c07e15c9eb9a574436360c86eda64f2997dc41ac` has the same src tree, but its old 47/59/16 and package results are not relabeled as runs of 8a13. The six intervening changed files are diagnostic and D2 tools. Existing default PackageSmoke run 41639653073 succeeded at a7a11c8502cf2c5207861fb0577ea633fccafc8a, not this target.

## Gap and bounded action

The existing PublicApi tool explicitly excludes assembly attributes. This lane builds and packs the frozen ten-project production graph, verifies the nine package/symbol pairs using unchanged frozen helpers, and inspects the nine exact lib DLLs plus the single SDK analyzer DLL. It executes no RPC consumer, D2 operation, cancellation regression, integration suite, or benchmark. It neither publishes packages nor changes grants.

An independent clean source checkout is used so repository commit and assembly informational versions remain the exact candidate SHA, separate from the tool/workflow SHA. SDK selection must be 10.0.114; observed CLI/host/runtime inventory is retained with hashes. A fresh private cache and local official NuGet feed configuration are used. Commands run through the existing bounded process owner. No retries are performed. Failure exits nonzero and retains only local diagnostics; the console contains fixed-category failure counts without stdout/stderr excerpts and a compact, size-limited metadata identity summary. There is no artifact uploader or raw log/trace upload.

Metadata inspection uses PEReader/MetadataReader, never loads target assemblies. It records full identity/public-key bytes, SHA256, MVID, informational version, assembly references, reference-assembly markers, complete IVT/access-check attribute strings and actual metadata rows. The source declaration manifest records duplicates, but validation compares complete strings as sets because the compiler may coalesce byte-identical repeated attributes. Actual emitted multiplicity is retained without claiming equality to source declaration multiplicity. Explicit unexpected grant modifiers fail. Public keys are not stripped. This is a source-to-package inventory check, not authorization or a declaration that all friends are desirable.

Synthetic tests cover malformed/negative metadata, actual duplicated metadata rows, constructor types, namespaces and CLI failure. Runner tests cover package analyzer ambiguity, exact identity/hash/version/source and missing/extra grants. Existing package extraction rejects ref/runtime/alternate-TFM/duplicate/case-variant shipping assets.

## Limits

This does not complete #805. It does not establish public API semantic correctness, 2.0.3 ABI compatibility, consumer loadability, Trim/AOT, security approval, admission/cancellation behavior, stability or performance. Existing previous evidence remains bounded to its exact source/tool identities. Final current-source package/ref/implementation/API/forwarder/consumer and release gates still need reconciliation.
