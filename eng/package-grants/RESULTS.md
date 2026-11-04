# #805 frozen package metadata result

**PASS for this bounded package inventory/grants check only. #805 is not closed.**

[Revision 2 run 41914187841](https://github.com/SunSi12138/SharpLink/actions/runs/41914187841), job 126869372741, completed successfully on 2026-11-04 at 21:16 UTC. Job status and retained console identity summary agree.

- Production source: `8a13b12e799bb70a989eacac0c1b4cea22ab80ae`; src tree `bfc211191d91b5a77c6a8f0085a0c00a459c5583`.
- Executed tools/workflow: `fe5d482a9fec60f3ad2065093628a91162b154a8`, independently checked out from production source.
- Producer and isolated metadata reader each selected SDK **10.0.114**, CLI host **10.0.14**. CLI identity is not a measurement of a production application runtime, since no such application ran.
- **11** runner tests and **44** reader metadata/CLI checks passed in that job.
- Ten production projects restored/built; nine package/symbol pairs passed frozen existing package checks. The exact nine `lib/net10.0` DLLs plus the one SDK analyzer were inspected without loading target assemblies.

## Observed package metadata

All ten assembly versions are `3.0.0.0`; all informational versions are `3.0.0+8a13b12e799bb70a989eacac0c1b4cea22ab80ae`. Package and assembly SHA256 values, MVIDs and exact source identities are bound in [RESULTS.json](RESULTS.json).

- **23 emitted InternalsVisibleTo rows**, matching the complete grant strings declared by the frozen source. Counts by assembly: Abstractions 5, Client 4, Hosting 1, Runtime 7, Serializer.SharpPack 1, Server 5; the other four assemblies have none.
- **0 IgnoresAccessChecksTo rows**, **0 reference-assembly markers**, and no explicit unexpected AllInternalsVisible modifier.
- **111 AssemblyRef rows** and **20 SDK type-forwarder rows** recorded. The forwarder targets recorded in this build are SharpLink.Abstractions 3.0.0.0. Recording these is not an ABI or compatibility verdict.
- The source manifest contains 30 declarations, including repeated identical attributes. The compiler emitted 23 rows. Validation intentionally compares complete strings as sets and retains actual emitted rows, rather than inventing seven metadata duplicates. Synthetic PE tests independently prove duplicate metadata rows are preserved by the reader.

A separate retained-summary cross-check verified all nine package-hash/assembly-hash bindings, the analyzer binding, source/version/MVID identities and grants. It checks evidence consistency; it is not a second physical re-read of package bytes after the hosted runner ended. The compact result preserves hashes and counts, not raw logs/traces. `fullMetadataCanonicalSha256` hashes the entire parsed `PACKAGE_GRANTS_SUMMARY` object, serialized with Python `json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True)` and encoded as UTF-8 without a trailing newline. Array order is preserved. Per-assembly canonical hashes apply the same procedure to the respective AssemblyReferences or TypeForwarders array.

## First failure retained

[First run 41912862444](https://github.com/SunSi12138/SharpLink/actions/runs/41912862444), tools `83a373bc488acee36195f0e41969ec653e9b5aa5`, failed at reader-self-tests despite command exit0 because the process owner reported one cleanup error. Actual package assembly inventory did not run. [FIRST-RUN.json](FIRST-RUN.json) preserves the failed result; it is not relabeled as passed.

Revision 2 explicitly disabled shared compilation and reusable MSBuild nodes for fixture builds, without weakening process ownership. The original process identity was not retained in the permitted compact evidence, so a compiler server remains an unconfirmed explanation. No unchanged-run retry was performed.

## Remaining boundaries

This fills the assembly-attribute inventory gap left by the existing PublicApi tool's `IncludeAssemblyAttributes=false`. It does not approve these grants as a security policy, modify permissions, complete source API semantic review, prove 2.0.3 ABI or runtime consumer compatibility, or satisfy Trim/AOT, stability, cancellation, admission or performance gates. References and forwarders are recorded only.

No RPC consumer was run, no D2 or denied cancellation regression was touched, no raw logs/traces or package binaries were uploaded, and no merge or release occurred. Earlier c07e15 or a7a11c85 test/package results remain evidence for their own exact identities, not automatic coverage of 8a13. Final #805 release acceptance still requires reconciliation of the outstanding API/ABI/consumer and other release gates at the chosen final source.
