# 0.9.x documentation and RC research

## Exact baseline

- Base branch: `dev`.
- Base commit: `4b662fc8a36b5a186cb80998986a27d6d529978e`, the merge commit for PR #19.
- Product version at the baseline: 0.8.44.

## Documentation inventory

- The repository contains roughly 292 version-specific audit, migration, and performance Markdown
  files across the Chinese and English trees. They are intermediate engineering evidence, not a
  usable 1.0 documentation set.
- Ten runnable demo scenarios currently cover hello world, streaming, cancellation, timeout,
  one-way calls, logging, hosting, and separated client/server deployment.
- Major implemented feature families without a focused runnable demo include security,
  resilience/discovery, compression, admission control, interceptors/telemetry, transport choice,
  and multi-cluster/dynamic modules.

## Public API documentation baseline

- Enabling `GenerateDocumentationFile` and treating CS1591 as an error over `src/` finds 266 unique
  missing public type/member comments.
- Breakdown: Abstractions 157, Runtime 58, Server 20, Client 16, Hosting 7, SDK 6, Generator 2,
  SharpPack adapter 0.
- The gate must apply to framework source projects only. Tests and demos intentionally declare
  public contracts for generated-code coverage and are not product API documentation.

## Public API documentation result

- The final source build reports zero CS1591 errors, zero XML documentation warnings, and zero
  compiler warnings overall.
- Each of the seven runtime NuGet packages contains the XML file matching its primary assembly.
- Generator 121/121, Unit 503/503, and Integration 252/252 pass after the documentation gate.
- No executable statement or public signature changed, so a performance comparison is not
  warranted for this documentation-only checkpoint; the exact RC performance matrix remains the
  authoritative performance acceptance step.

## Acceptance model

- CS1591 count is exactly zero for every project under `src/`.
- XML documentation files are present beside every shipped assembly and inside each applicable
  NuGet package.
- A feature matrix maps every supported behavior and material limit to current documentation and
  at least one runnable demo or an explicit non-demo rationale.
- Old version-by-version audit/performance/migration evidence is removed after current guidance
  has absorbed every still-relevant behavior and limit.
- All documentation links, code snippets, demos, packages, tests, AOT and performance gates are
  validated on the exact RC candidate.
