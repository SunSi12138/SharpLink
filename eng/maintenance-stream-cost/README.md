# Matched TLS small-item streaming observations

This authored probe measures real authenticated TLS client-to-server integer streams on an actual two-connection pool. Both arms use the same generated contract and service, 32,768 integer items, explicit `[Timeout(3600)]`, and a 4,096-byte server stream-credit window. Before each measurement the producer is held after the first item is consumed, and the existing server map must contain exactly one live cooperative call with a finite unexpired deadline. No per-item diagnostic counter or owner lookup is added to product source. The service counts and sums received values identically in both arms.

The timer and process-managed-allocation window starts when the producer is released and ends when the caller observes the RPC result. Each process retains two warmups and five subsequent samples. Three fresh processes per arm/mode use alternating arm order. FullOpts and tiered PGO with explicitly accelerated promotion are separate modes. There is no disassembly I/O during these elapsed observations. Native evidence is a separate campaign. Process allocation includes framework, TLS, application and background work; it is not caller-thread helper allocation or whole-lifetime/worker allocation. No throughput-equivalence test, regression budget, or general performance promise is made.

The candidate has populated maintenance enrollment and negotiated capability but does not perform an update in this streaming workload. A separate enabled-TLS ordinary-call probe validates held-proxy pre/post update behavior. All samples verify unchanged physical/session objects, pool cardinality two, authentication count two and TLS. The healthy callback-owner table must remain unallocated.

## Integrated source profiles

Use `--profile integrated-prerequisites` to compare `ac17c8d8` with the combined
candidate, or `--profile integrated-dev` for `cc7c458b` versus that same candidate.
Use separate fresh probe directories and preserve each profile's provenance. The
three immutable source identities are in `eng/maintenance-integration-sources.json`.
The existing baseline directory name remains `published-prerequisites`; its actual
commit and profile are recorded in `source-provenance.json`. These profiles do not
reuse the historical numerical results below. Source preparation alone executes
no timing workload. Formal acceptance still requires the dev/prerequisite/candidate
comparison; a favorable pair cannot stand in for the other arm.

## Reproduce the historical profile

Use Linux x64, Python 3 and official SDK10.0.102. The included global.json rejects SDK roll-forward. Provision runtimes separately; these scripts do not install software. Build both arms with the same SDK. The existing prerequisite preparation script fetches nothing and uses immutable #928/#931/#932 commits already available in the supplied checkout, validating the complete prerequisite production manifest. It copies no repository test project.

1. Fetch the three full SHAs documented in `prepare_public_arms.py`, then run `python prepare_public_arms.py --repo /path/to/repository --candidate FINAL_COMMIT`.
2. Run `python capture_build_toolchain.py` and `bash build_arms.sh published-prerequisites candidate` with SDK10.0.102 selected.
3. Select an isolated official runtime host in PATH and DOTNET_ROOT, containing exactly one host/fxr. Run `python run_stream.py --run-name runtime-10.0.2 --expected-runtime 10.0.2`, then `python summarize_stream.py --group runtime-10.0.2`. Repeat with runtime10.0.13.
4. Preserve the full evidence directories, including failed launches, warmups, every sample, source/DLL hashes and actual runtime binary hashes. Do not average away unfavorable results or interpret an absent native body as zero cost.

Reference measurement source: ed5f17db02d80ddd23c14338aff524f8971e8ca3, src c242f33f0041439b176c43460864a888237b79d9; the adopted delivery has the identical production subtree. Prerequisite4471526522d9b9a7bcf4fbde1b5c50faa63d8f8e is reproduced from the published physical+pump prerequisites. The no-ref callback correction is included in the feature delta, not hidden in the baseline.
