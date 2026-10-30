# Test project-reference boundaries

This note defines the repository-owned `ProjectReference` topology whose **origin** is under `test/`. The machine-readable source of truth is [`test-project-reference-boundaries.yml`](test-project-reference-boundaries.yml). If this note and the YAML disagree, the YAML wins.

This policy is the test-side companion to [`project-reference-boundaries.md`](project-reference-boundaries.md) and the production policy introduced for #371. It does **not** weaken or extend the production graph: any `ProjectReference` whose origin is under `src/` remains governed only by [`project-reference-boundaries.yml`](project-reference-boundaries.yml), including its rule that production projects may not reference test projects.

## Scope and intent

The guardrail is closed-world for test-originating `ProjectReference` edges, not for every kind of test dependency:

- enumerate every `*.csproj` under `test/`, including `test/fixtures/**`;
- inspect every `ProjectReference` originating from those projects, including repository-owned imported declarations;
- require every such origin, target, edge, and reference mode to be authorized by the YAML;
- allow package-only test projects to exist without registration because `PackageReference` topology is out of scope;
- deny a `ProjectReference` from an unregistered test project, to an unregistered test target, or to any project outside the registered production/test project sets;
- keep `src/` production rules from #371 authoritative and independent.

The baseline inventory was captured from `dev` at `3e772ef0de75bda9f79d5f3508ef01f573bb6254` with **107** test-originating `ProjectReference` edges. The reviewed policy now lists **149** edges: 40 references already present on dev were missing from the historical policy, physical ownership adds one allocation-probe build dependency, and cooperative maintenance adds one build-only DynamicServices dependency. No dependency is added by this policy reconciliation, and there is no debt exception.

## Reference modes

Mode is part of the architecture policy. The same source/target pair in a different mode is a different edge and is denied unless explicitly listed.

### `assembly`

A normal compile/runtime project reference:

- effective `ReferenceOutputAssembly` is `true` (including the normal omitted/default value);
- `OutputItemType` is not `Analyzer`.

### `analyzer`

A source-generator/analyzer-only reference:

- `OutputItemType="Analyzer"`;
- `ReferenceOutputAssembly="false"`.

The common test pattern `... -> SharpLink.Generator` uses this mode. `SharpLink.Generator.Tests -> SharpLink.Generator` is intentionally different: it is an `assembly` edge because that project tests the generator implementation directly.

### `build_only`

A project that must participate in the build graph without becoming a compile-time assembly reference:

- `ReferenceOutputAssembly="false"`;
- `OutputItemType` is not `Analyzer`.

This is intentional test infrastructure, not a weaker form of `assembly`. Examples include IntegrationTests building AOT/dynamic-plugin artifacts and Benchmarks building `SharpLink.DynamicServices` before copying its output DLL.

As in the production policy, `ReferenceOutputAssembly` and `OutputItemType` are architecture-bearing metadata: conditions may gate whether an authorized item is active, but conditions, property expansion, item definitions, or repository-owned updates must not be used to vary the authorized mode.

## Classification

Every current edge belongs to exactly one policy class:

- `allowed`: a test project directly references a production project for the code under test, SDK use, or direct generator testing;
- `intentional_test_infrastructure`: a test project references another repository-owned test/support project to share contracts, services, plugins, load-test infrastructure, package-rooting fixtures, or build artifacts;
- `debt_exception`: an otherwise disallowed current edge temporarily retained with a reason and tracking issue.

There are currently **no `debt_exception` edges**. A future exception must be added explicitly to `debt_exceptions` with both a rationale and a tracking issue; an exception is not precedent for neighboring edges.

## Historical inventory

The following table is explanatory; the YAML is normative. Unmarked targets use `assembly` mode. `analyzer` and `build-only` are called out explicitly. Test-project targets are prefixed with `test:`.

| Origin | Production targets | Test/support targets |
| --- | --- | --- |
| `SharpLink.AotContracts` | `Abstractions`, `Sdk`, `Generator` (analyzer) | — |
| `SharpLink.AotServices` | `Sdk`, `Generator` (analyzer) | `test:AotContracts` |
| `SharpLink.AotSmoke` | `Abstractions`, `Runtime`, `Client`, `Server`, `Sdk`, `Serializer.SharpPack`, `Generator` (analyzer) | `test:AotContracts`, `test:AotServices` |
| `SharpLink.Benchmarks` | `Abstractions`, `Server`, `Client`, `Runtime`, `Sdk`, `Serializer.SharpPack`, `Generator` (analyzer) | `test:DynamicContracts`, `test:LoadTestBase`, `test:DynamicServices` (build-only) |
| `SharpLink.ChaosTests` | `Abstractions`, `Runtime`, `Client`, `Server`, `Sdk`, `Generator` (analyzer) | — |
| `SharpLink.CodecCompatibility` | `Runtime` | — |
| `SharpLink.CodecCompatibility.Android` | `Runtime` | — |
| `SharpLink.CodecCompatibility.Browser` | `Runtime` | — |
| `SharpLink.CodecCompatibility.iOS` | `Runtime` | — |
| `SharpLink.DynamicContracts` | `Sdk`, `Abstractions`, `Serializer.SharpPack`, `Generator` (analyzer) | — |
| `SharpLink.DynamicServices` | `Sdk`, `Abstractions`, `Generator` (analyzer) | `test:DynamicContracts` |
| `SharpLink.Generator.Tests` | `Generator` | — |
| `SharpLink.IntegrationTests` | `Abstractions`, `Runtime`, `Client`, `Server`, `Sdk`, `Serializer.SharpPack`, `Generator` (analyzer) | `test:AotSmoke` (build-only), `test:DynamicContracts` (build-only), `test:DynamicServices` (build-only) |
| `SharpLink.LoadTestBase` | `Abstractions`, `Runtime`, `Client`, `Server`, `Serializer.SharpPack` | — |
| `SharpLink.LoadTest` | `Abstractions`, `Runtime`, `Client`, `Server`, `Sdk`, `Serializer.SharpPack`, `Generator` (analyzer) | `test:LoadTestBase` |
| `SharpLink.LoadTest.Tests` | — | `test:LoadTest` |
| `SharpLink.MultiClusterTest.Contracts` | `Sdk`, `Generator` (analyzer) | — |
| `SharpLink.PackageSmoke` | — | `test:SdkOnlyPackageSmoke`, `test:ReferenceRooting.PackageServer` (build-only), `test:ReferenceRooting.PackageClient` (build-only) |
| `SharpLink.PreCreditAotSmoke` | `Abstractions`, `Runtime`, `Client`, `Server`, `Sdk`, `Generator` (analyzer) | — |
| `SharpLink.ReferenceRooting.PackageServices` | — | `test:ReferenceRooting.PackageContracts` |
| `SharpLink.ReferenceRooting.PackageServer` | — | `test:ReferenceRooting.PackageServices` |
| `SharpLink.ReferenceRooting.PackageClient` | — | `test:ReferenceRooting.PackageContracts` |
| `SharpLink.RollbackPlugin` | `Abstractions` | — |
| `SharpLink.StaticCodecOwnerTest.Contracts` | `Sdk`, `Generator` (analyzer) | — |
| `SharpLink.StreamLoadTest` | `Abstractions`, `Server`, `Client`, `Runtime`, `Sdk`, `Serializer.SharpPack`, `Generator` (analyzer) | `test:LoadTestBase` |
| `SharpLink.StreamLoadTest.Tests` | — | `test:StreamLoadTest`, `test:LoadTestBase` |
| `SharpLink.UnitTests` | `Abstractions`, `Client`, `Hosting`, `Runtime`, `Sdk`, `Serializer.SharpPack`, `Server` | `test:MultiClusterTest.Contracts`, `test:RollbackPlugin`, `test:StaticCodecOwnerTest.Contracts`, `test:ReadOwnershipAllocationProbe` (build-only) |
| `test/fixtures/generated-api4/source/SharpLink.Api4Fixture` | `Sdk`, `Generator` (analyzer) | — |

Current test projects with no outgoing `ProjectReference` are still valid. In particular, package/version fixtures such as `SharpLink.AbstractionsPackageSmoke`, `SharpLink.HostingPackageSmoke`, `SharpLink.SdkOnlyPackageSmoke`, the API3 fixture, the generated-ABI-mixing fixtures, and the protocol-v2 cross-version fixture use package dependencies rather than project dependencies. `SharpLink.ReferenceRooting.PackageContracts` and `SharpLink.SdkOnlyPackageSmoke` are registered in the YAML because other test projects target them. `SharpLink.GeneratedAssemblyScanner` currently has no project edge in either direction.

## Intentional test infrastructure

The test-to-test edges are deliberately explicit rather than covered by a broad rule such as “tests may reference tests.” This keeps unrelated test suites from silently coupling to each other.

Important current support roles include:

- `SharpLink.MultiClusterTest.Contracts`: generated contracts consumed by UnitTests for multi-cluster scenarios;
- `SharpLink.RollbackPlugin`: separately built plugin assembly consumed by rollback/dynamic-module tests;
- `SharpLink.StaticCodecOwnerTest.Contracts`: generated contracts used by static-codec ownership tests;
- `SharpLink.ReadOwnershipAllocationProbe`: a standalone reader-allocation child host built and copied by UnitTests. Its `ReferenceOutputAssembly="false"` edge is `build_only`, not an assembly or analyzer dependency; the child loads the tested module at execution time rather than creating a reverse project-reference edge;
- `SharpLink.DynamicContracts` / `SharpLink.DynamicServices`: collectible/dynamic-module artifacts; some consumers compile against contracts while only building the service assembly;
- `SharpLink.LoadTestBase`: shared load-test infrastructure used by both load executables and their tests;
- `SharpLink.ReferenceRooting.*` and `SharpLink.SdkOnlyPackageSmoke`: package-rooting/build-order fixtures;
- AOT contracts/services/smoke projects: explicit NativeAOT build/test artifacts rather than general-purpose test libraries.

These roles explain the current edges; they do not authorize another test project to consume the same support project automatically. A new direct edge requires a policy change.

## Relationship to the production policy (#371)

The two policies compose by **origin**:

1. `src/**` origin: only `project-reference-boundaries.yml` decides whether the edge is legal.
2. `test/**` origin: only `test-project-reference-boundaries.yml` decides whether the edge is legal.
3. Production project IDs referenced by the test policy come from the production policy's `projects` registry; the test policy does not duplicate or redefine production project paths.
4. The test policy cannot authorize `src -> test`, cannot add a production edge, and cannot change a production edge's mode.
5. Package dependency closure and demo/sample topology remain separate concerns.

This preserves #371 as the single production architecture source of truth while allowing tests to depend on production code in controlled, explicit ways.

## Mechanical interpretation

A later guard can enforce the YAML without inferring test intent:

1. Load the production and test policy YAML files.
2. Enumerate every `*.csproj` under `test/`, including fixtures.
3. Build the repository-owned MSBuild declaration closure for every test project, following statically traversable repository imports regardless of import conditions.
4. Enumerate every declared `ProjectReference` without using its `Condition` to suppress authorization checking. The target path must be literal and resolvable.
5. Resolve the origin to `test_projects`. Any unregistered test project that declares a `ProjectReference` fails closed.
6. Resolve the target either to the production policy's `projects` registry or to `test_projects`. Any other target fails closed.
7. Expand every grouped `to` list in `allowed_references` into exact independent edges and match `(from, target_scope, to, mode)`.
8. Validate mode-bearing metadata as context-invariant and classify it as `assembly`, `analyzer`, or `build_only` using the YAML semantics.
9. Reject an unlisted edge, a target-scope mismatch, a mode mismatch, a dynamic/unresolved include, or an unauthorized imported/conditioned declaration.
10. Separately use normal MSBuild evaluation to validate active items and imported behavior; evaluation must not hide a potentially forbidden declaration.
11. Treat `debt_exceptions` exactly like explicit production exceptions: every entry needs a reason and tracking issue and authorizes only that exact edge/mode.
12. Do not infer authorization from transitive reachability, shared test naming, solution membership, or the fact that another test already references the target.

This issue defines the design only. It intentionally does **not** add or change CI enforcement; a later guard can consume this policy as its input.

## Changing the boundary

A PR that intentionally adds, removes, or changes a test-originating `ProjectReference` should update the YAML in the same change. Normal test-to-production usage belongs in `allowed`; shared test/support wiring belongs in `intentional_test_infrastructure`; an architecture violation must not be normalized as an exception without a specific reason and tracking issue.

## Current inventory reconciliation

The historical 107-edge inventory was not kept current with later dev additions.
A direct literal `ProjectReference` declaration census on dev `57f06f17` finds
147 edges; the physical-ownership allocation child and cooperative-maintenance
DynamicServices build edge bring this source tree to 149. The table above records
the historical support roles; the normative YAML explicitly registers all current
projects and edges. No analyzer rule, wildcard authorization, production boundary,
project reference, or debt exception changes. This declaration check is not an
MSBuild evaluated-item or runtime test result.

The 40 preexisting omissions and one new maintenance edge are enumerated below.
In particular, Compression.Zstd references already exist on dev; they are not
introduced by cooperative maintenance.

| Origin | Target | Mode | Source |
| --- | --- | --- | --- |
| `aot_smoke` | `compression_zstd` | `assembly` | already on dev57 |
| `benchmarks` | `compression_zstd` | `assembly` | already on dev57 |
| `codec_compatibility_layout_evidence` | `runtime` | `assembly` | already on dev57 |
| `generator_tests` | `abstractions` | `build_only` | already on dev57 |
| `integration_tests` | `hosting` | `assembly` | already on dev57 |
| `integration_tests` | `module_dependency_consumer` | `build_only` | already on dev57 |
| `integration_tests` | `referenced_codec_consumer` | `build_only` | already on dev57 |
| `integration_tests` | `referenced_codec_provider` | `build_only` | already on dev57 |
| `integration_tests` | `replacement_services` | `build_only` | already on dev57 |
| `module_dependency_consumer` | `abstractions` | `assembly` | already on dev57 |
| `module_dependency_consumer` | `referenced_codec_provider` | `assembly` | already on dev57 |
| `producer_progress_aot_evidence` | `abstractions` | `assembly` | already on dev57 |
| `producer_progress_aot_evidence` | `client` | `assembly` | already on dev57 |
| `producer_progress_aot_evidence` | `generator` | `analyzer` | already on dev57 |
| `producer_progress_aot_evidence` | `runtime` | `assembly` | already on dev57 |
| `producer_progress_aot_evidence` | `sdk` | `assembly` | already on dev57 |
| `producer_progress_aot_evidence` | `serializer_sharppack` | `assembly` | already on dev57 |
| `producer_progress_aot_evidence` | `server` | `assembly` | already on dev57 |
| `referenced_codec_consumer` | `abstractions` | `assembly` | already on dev57 |
| `referenced_codec_consumer` | `referenced_codec_provider` | `assembly` | already on dev57 |
| `referenced_codec_provider` | `abstractions` | `assembly` | already on dev57 |
| `replacement_contracts` | `abstractions` | `assembly` | already on dev57 |
| `replacement_contracts` | `generator` | `analyzer` | already on dev57 |
| `replacement_contracts` | `sdk` | `assembly` | already on dev57 |
| `replacement_contracts` | `serializer_sharppack` | `assembly` | already on dev57 |
| `replacement_services` | `abstractions` | `assembly` | already on dev57 |
| `replacement_services` | `generator` | `analyzer` | already on dev57 |
| `replacement_services` | `replacement_contracts` | `assembly` | already on dev57 |
| `replacement_services` | `sdk` | `assembly` | already on dev57 |
| `sharp_pack_aot_smoke` | `abstractions` | `assembly` | already on dev57 |
| `sharp_pack_aot_smoke` | `aot_external_payloads` | `assembly` | already on dev57 |
| `sharp_pack_aot_smoke` | `client` | `assembly` | already on dev57 |
| `sharp_pack_aot_smoke` | `generator` | `analyzer` | already on dev57 |
| `sharp_pack_aot_smoke` | `runtime` | `assembly` | already on dev57 |
| `sharp_pack_aot_smoke` | `sdk` | `assembly` | already on dev57 |
| `sharp_pack_aot_smoke` | `serializer_sharppack` | `assembly` | already on dev57 |
| `sharp_pack_aot_smoke` | `server` | `assembly` | already on dev57 |
| `unit_tests` | `compression_zstd` | `assembly` | already on dev57 |
| `unit_tests` | `dynamic_services` | `build_only` | #933 new build dependency |
| `zstd_aot_smoke` | `compression_zstd` | `assembly` | already on dev57 |
| `zstd_aot_smoke` | `runtime` | `assembly` | already on dev57 |

### Architecture review of previously omitted references

The 40 previously existing edges are grouped by their actual test purpose:

- **Zstd (5):** unit and benchmark provider coverage plus AOT host/provider/runtime
  wiring. Compression remains an explicit optional test dependency.
- **Layout evidence (1):** the shared raw-layout executable consumes Runtime.
- **Generator build-only Abstractions (1):** `RealAbstractionsCompilationReference`
  supplies the real DLL by path to specific Roslyn tests, without contaminating
  synthetic SDK compilation references.
- **Hosting (1):** `HostedServiceProviderIntegrationTests` exercises hosted ownership.
- **Codec/module fixtures (8):** isolated ALC exact-generation and dependency-order
  controls; the three Integration dependencies remain build-only.
- **Replacement fixtures (9):** contracts/services deliberately share assembly names
  across distinct RPC identities; Integration only builds the replacement DLL.
- **Producer-progress AOT host (7):** an existing explicit generated-contract host.
- **SharpPack AOT host (8):** an existing host with a separate external-payload fixture.

The one new maintenance edge is independently justified by
`PreparedImplementationReplacementTests.Support.cs`: it locates the dynamic service
DLL by path and loads it in a collectible ALC. `ReferenceOutputAssembly=false` is
retained; the test assembly does not gain a static reference to the dynamic module.

All production and test project files retain their original references. Production
references never target `test/`; GenerationControl still depends only on Abstractions
and Generator and gains no incoming project edge. Frozen API3/API4 binary fixtures
remain decoded from their provenance-checked payloads, not rebuilt from current
source. Their preexisting API4 source recipe edges are unchanged. The policy still
default-denies undeclared projects, edges and modes; no blanket exception is added.
