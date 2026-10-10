# #739 NativeAOT feasibility pilot

This is a bounded feasibility experiment over unchanged production source
`eb99fe887cf2129d9b88441245ca0a4a6406b6c2`. It makes no stable-performance,
nonregression, JIT-parity, cancellation/fault, or formal owner-closure claim.

## Run

On Linux x64 with the official SDK `11.0.100-rc.1.26425.128`, matching runtime
`11.0.0-rc.1.26425.128`, clang and its bfd linker available:

```sh
python3 eng/validation/issue739/net11/Aot/test_aot.py
python3 eng/validation/issue739/net11/Aot/run_aot.py \
  --output artifacts/issue739/dev-net11-aot --dotnet dotnet
```

The script has a 1380-second total budget, leaving setup/upload room in a
25-minute hosted job. A missing/unsupported SDK, package, compiler, warning,
lowering, native input, scheduling shape, or counter fails closed and retains
the exact diagnostic. Do not loosen the gate or retry a different toolchain
inside this experiment.

## Fixed shape

- Eight cases: wrapped/direct synchronous reader; wrapped/direct forced-incomplete
  serial reader; wrapped/direct forced-incomplete burst of 32 distinct readers;
  TCP concurrency-one Add and raw control.
- Every case uses ABBA then separate same-binary AA, in 48 fresh processes total.
- Reader: 131072 operations and 16384 warmup operations. TCP: 8192 and 2048.
- Identical original package references and projected package-pruning disabled in
  both arms; production and generated Fixture lowering toggles only.
- The A traditional consumer DLL/PDB is reused in B's bin and RID-specific obj.
  The independent B consumer is archived. Binding configurations must match.
  Native images are separately compiled; their hashes may differ or coincide.
- Exact pre-AOT metadata proves A traditional and B runtime-async for the actual
  pooled reader, while retaining its builder attribute and semantic signature.
  No production attribute edits or automatic custom-builder opt-out assumption.
- Native execution requires `RuntimeFeature.IsDynamicCodeSupported=false`,
  `IsDynamicCodeCompiled=false`, and an ELF64 x86-64 `Environment.ProcessPath`
  whose SHA-256 also matches an external hash. No assembly-location assumptions.
- Before/after ILC snapshots and the actual ILC response file must identify the
  exact metadata-proven consumer, Fixture and production inputs. A managed
  compiler invocation during `--no-build` publish is rejected.

The copied reader lifecycle, completion, consumption, advancement, thread, and
version-wrap counters are exact. Reader warmup and the one-second scheduling
pause are retained; NativeAOT has no JIT tiering here. Tiny's actual Worker,
Drain, sentinel, 20ms tail and measurement boundary are retained. TCP control
includes its sentinel. All bytes are gross, with no subtraction or outlier
removal. Explicit JSON writing replaces reflection serialization only.

`copy-origins.json` pins the original sources. The runner checks the copied
reader mechanics and Tiny measured window/Worker/Drain byte-for-byte before
building. Existing Driver, ReaderControl, Fixture, Metadata and run_pilot.py
remain unchanged.

## Evidence and limitations

Keep root logs/binlogs, provenance, lowering metadata/proof, commands, all sample
JSON and `proof/`. `proof/` includes actual consumed project IL, pre-AOT bin/obj
inputs, before/after snapshots, response files, restore assets, overlays and
generated source. Native outputs under `native/` can be uploaded separately.
`work/` may be excluded from the artifact. Package/compiler/reference-pack
files are hash-manifested with exact versions and selected paths; their entire
bytes are not duplicated in `proof/`. Actual ILC, CoreLib and clang/bfd selection
must be tied to the pinned identities.

Local SDK10 tests are compile/correctness checks only. The production entry
rejects JIT execution. A temporary local-only entrypoint can directly call the
same ReaderCases/TinyCases methods to verify counters and JSON; its output says
`local-correctness-only` and fails the native sample validator. It is never
included in the hosted source projection or accepted as NativeAOT evidence.
