# 0.9.x documentation and RC status

- PR #19 merged 0.8.x into `dev` at `4b662fc8a36b5a186cb80998986a27d6d529978e`.
- The 0.9.x worktree is isolated on `feature/0.9-docs-rc`.
- Version is being advanced to 0.9.0 for the documentation/API stabilization series.
- Source-only XML documentation enforcement reduced the 266-member pre-fix compiler witness to
  zero. Release rebuild completes with zero warnings and errors.
- All seven 0.9.0 runtime packages contain their matching XML documentation file. Generator
  121/121, Unit 503/503, and Integration 252/252 pass.
- The current documentation set contains 18 stable topic files; all 301 version-specific
  architecture/audit/migration/performance/chaos reports have been removed.
- Seven new runnable demos cover security, compression, admission, interceptor/telemetry,
  resilience, five transports, and two-assembly multi-cluster routing. All execute successfully.
- The 41-project Release solution rebuild and Markdown local-link validation pass with zero build
  warnings/errors and zero missing local links.
- The next phase validates packages, PackageSmoke, NativeAOT, Chaos and the full existing demo set
  before freezing `1.0.0-rc1`.
- No product behavior change or performance claim has been made in this phase yet.
