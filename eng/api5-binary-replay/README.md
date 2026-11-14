# Real API5 to API6 registration boundary

Reuses the historical-source replay from closed/unmerged #974, updated for API6.
Run with a clean checkout of immutable `d5665d057e18f9b39fe80bcc5911900eb4663ce2`,
a clean candidate checkout, a new output directory and the candidate's full commit:

```text
python eng/api5-binary-replay/verify-real-api5-binary.py OLD_ROOT CURRENT_ROOT OUTPUT CURRENT_COMMIT
```

Use the repository SDK for both arms. The runner builds each arm's real packages
and compiles the same Echo contract with that arm's unmodified packaged generator.
Separate processes first prove API5/API5 and API6/API6 public RegisterAssembly
success, one real generated Stub and released references. API6 then consumes the
actual old plugin DLL and must return IncompatibleManifest naming both exact ABI
identities. It must not fall through to InvalidManifest, TypeLoadException or a
different registration failure.

This rebuilds historical source; it is not a retained binary from the original
historical CI run. No generated-source copy, Reflection.Emit, binary rewrite,
manifest rewrite or business RPC substitutes for the controls. No performance is
measured. Hosts, generated sources, package/restore graphs and all logs remain
in the new output directory, including on failure. Package identities are checked
with the existing package gate; source commit checks bind both clean checkouts.

The dedicated workflow preserves this evidence on every relevant PR. It does not
change or reopen the paused #387 investigation.
