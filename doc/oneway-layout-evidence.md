# OneWay layout and native code evidence

This investigation-only helper inspects a built `SharpLink.OneWayEvidence.dll`.
It has no production project references and does not run or modify RPC code.
Run the helper under the same architecture/runtime as the measured executable.
Use Release builds: a Debug async state machine may be a class, in which case
`sizeof` measures a reference slot rather than its object payload.

## Build and measure metadata

```bash
dotnet build test/SharpLink.OneWayLayoutEvidence -c Release
dotnet test/SharpLink.OneWayLayoutEvidence/bin/Release/net10.0/SharpLink.OneWayLayoutEvidence.dll --self-test
python3 eng/oneway-codegen-evidence.py --self-test

dotnet test/SharpLink.OneWayLayoutEvidence/bin/Release/net10.0/SharpLink.OneWayLayoutEvidence.dll \
  --assembly /absolute/path/to/arm/test/SharpLink.OneWayEvidence/bin/Release/net10.0/SharpLink.OneWayEvidence.dll \
  --label C --source-sha "$EXACT_SOURCE_SHA" \
  --generated-dir /absolute/path/to/arm/test/SharpLink.OneWayEvidence/obj/generated/SharpLink.Generator \
  --output artifacts/oneway/C-layout.json
```

Each arm must be built from its intended source tree. Preserve the source SHA,
working patch if any, runtime version, architecture and assembly hashes.
The helper resolves dependencies beside the fixture or through its `.deps.json`;
keep those files together. A copied fixture with a substituted client assembly
is useful only for unchanged generated ABI, such as the A/C internal split. B's
new generated entries require the fixture to be rebuilt using B's generator.

The helper decodes closed generic calls in actual generated proxy method IL,
rather than substituting a hand-written stream-writer type. It measures:

- Exact generated request and writer types, managed inline sizes and fields.
- Exact closed async state-machine types, fields, offsets and managed inline size.
- Kickoff/dispatcher and `MoveNext` IL sizes and instruction counts.
- Conditional branch instructions excluding `switch`; separate switches/targets;
  and all branch-flow instructions including unconditional branches and `switch`.
- `HasClientStreams` getter calls in each IL body.
- Aggregate core `MoveNext` IL and kickoff/dispatcher IL. Splitting can reduce
  each executed body while increasing the total number of method definitions.
- Public/protected member signature manifests and hashes, OneWay entry signatures,
  generated type/method counts, generated IL bytes and generated source hashes.

`sizeof` uses emitted CLI `sizeof` against the closed runtime type. Field offsets
are measured against a boxed instance's unboxed data; no unmanaged marshaling
layout is substituted. Inline state-machine size is **not allocated bytes per
call**: object headers, async boxes, pooling, synchronous completion and unrelated
allocations require separate measurement. Public-member signature comparisons
help expose API/ABI growth but are not a replacement for binary compatibility
or old-generator/old-runtime smoke tests.

## JIT code

Exercise the real client path in a fresh process for each stream count. Capture
code separately from timed samples: disassembly changes diagnostic overhead.
Disable tiering and ReadyToRun so code listings are directly comparable FullOpts
JIT compilations. For this executable, the following tested filter captures the
async state's `MoveNext` as well as OneWay kickoff and wrapper methods:

```bash
COMPlus_TieredCompilation=0 DOTNET_ReadyToRun=0 \
COMPlus_JitDisasm='*OneWay*:MoveNext *InvokeOneWay*' \
COMPlus_JitStdOutFile="$PWD/artifacts/oneway/C-0.asm" \
dotnet /absolute/path/to/arm/SharpLink.OneWayEvidence.dll \
  tcp 0 1 0.15 0.1 3 none artifacts/oneway/C-0-diagnostic-run.json

python3 eng/oneway-codegen-evidence.py \
  --jit-log artifacts/oneway/C-0.asm --output artifacts/oneway/C-0-native.json
```

Repeat for stream counts 1 and 2 and each arm. Preserve the raw `.asm` files.
Check that `coreMoveNextCount` is nonzero and `complete` is true; an absent method
is not zero code size. The parser retains each native method's tier annotation,
byte count and conditional branch instruction count, including cold/finally
blocks present in the listing. Do not sum different tiers as one method size.
The diagnostic loop's timing is not performance evidence.

## Linux NativeAOT code

Publish the runtime fixture, not this reflection-only metadata helper:

```bash
dotnet publish test/SharpLink.OneWayEvidence -c Release -r linux-x64 \
  -p:PublishAot=true -p:StripSymbols=false -o artifacts/oneway/C-aot
python3 eng/oneway-codegen-evidence.py \
  --native-binary artifacts/oneway/C-aot/SharpLink.OneWayEvidence \
  --raw-dir artifacts/oneway/C-aot-code --output artifacts/oneway/C-aot-native.json
```

If symbols were separated, pass `--symbols path/to/SharpLink.OneWayEvidence.dbg`.
The script reads sizes/addresses from `nm`, preserves the full symbol listing and
`size -A` section listing, and disassembles every OneWay text symbol from the
actual executable using `objdump`. It distinguishes the whole executable's file
size from the sum of unique OneWay symbol address/size pairs. Symbols may alias;
the unique-pair sum avoids identical aliases but is not a general link-map
accounting of overlapping ranges. Compare `.text`, total binary size and relevant
method symbols separately. Generic sharing, trimming, alignment and unrelated
runtime roots can affect native results. A failed publish or absent symbols is
an explicit missing result, never evidence of no native growth.
