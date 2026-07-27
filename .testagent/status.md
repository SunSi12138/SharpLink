# 0.8.25 test status

## Evidence status

- Five P2 candidates identified against clean 0.8.24 commit `09d6078`.
- Complete pre-fix Generator run: 94 total, all 88 existing tests passed, and exactly six new probes failed across the five recommendations (generated identity includes separate hint and nested-peer probes).
- Evidence includes the exact CS8785 duplicate hint exception, invalid generated keyword syntax, absent SHARPLINK052/053/054 diagnostics, and duplicate nested `IInner_Proxy` declarations.

## Current gate

- Focused Generator 96/96 passes after implementation and assertion/pseudo-mutation strengthening.
- Review covers sanitized-name hint collisions, two nested peers, keyword method/payload/options/cancellation identifiers, every by-ref shape, static methods, properties/indexers/events, public reachability, generic containing types, and allowed default members.
- A 40-contract/400-method, 101-sample Generator A/B measured 15.953 -> 13.577 ms; compiler-thread allocation increased a bounded 0.14%, and no runtime source changed.
- Exact final tree passed a non-incremental Release build with 0 warnings/errors, Generator 96/96, Unit 449/449, Integration 237/237, seven-package pack, SDK analyzer-content inspection, and fresh-cache package smoke resolving 0.8.25 packages.
- Version and Chinese/English audit, migration, performance, changelog, README, SDK XML, and analyzer-release documentation are complete. The local 0.8.25 commit is ready.
- Consecutive complete audit rounds without a new improvement: 0/3.
