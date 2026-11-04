# #805 frozen package forwarding-hop result

## Verified hosted result

[Run 41920333043](https://github.com/SunSi12138/SharpLink/actions/runs/41920333043), job 126890827598, completed successfully on 2026-11-04 at 21:54:45 UTC. The job status and its actual completion/summary markers agree. Exactly one workflow was triggered.

- Production source: **8a13b12e799bb70a989eacac0c1b4cea22ab80ae**; src tree bfc211191d91b5a77c6a8f0085a0c00a459c5583.
- Executed tools/workflow: **e89bf38144a9145efed7fb794ff5be8c470c596e**. All nine published tool/source files and workflow were read back and matched reviewed local Git blob identities before execution was assessed.
- Producer and isolated probe SDK: **10.0.114**. Observed probe execution runtime: **10.0.14**. These are separately observed identities, not a minimum-version guarantee.
- Three production projects restored/built: Abstractions, Generator, SDK. Two product package/symbol pairs packed: Abstractions and SDK. Existing unchanged per-package identity and unique-DLL extraction helpers passed.
- **31/31 synthetic self-tests** passed with the exact pinned case-name→expected-error manifest. **6 Python runner tests** passed hosted.
- **20/20 actual package-extracted forwarding types** passed exact metadata target identity plus runtime type-reference identity against directly loaded Abstractions types. Loaded paths, SHA-256, MVID, assembly full identity and source informational version were checked. Runtime load context held exactly the two target assemblies.
- **Zero RPC processes**. No production source, IVT grant or permission change. No merge or release. No raw logs, traces, binaries or packages uploaded as artifacts.

## Exact two package identities

| Item | SHA-256 |
| --- | --- |
| SharpLink.Abstractions.3.0.0.nupkg | 45d0753ccc7344167bb7aee792f4f153e07eac16676e2578f5547f8945ee503d |
| Extracted Abstractions DLL | 47f7cf34f76bb1c8f55f8b36899798381149f99ef3210bef5181e6c2336bdba1 |
| SharpLink.Sdk.3.0.0.nupkg | 831fa2cb7cff2a15be1c28441f91831c519cc89be53e639369b672c37bdef25e |
| Extracted SDK DLL | 862b22c379caba0ac7b8fc484efd18c071e109844ae3c79a2a033f00083b68c4 |

Both assemblies have version 3.0.0.0, neutral culture, no public-key token, and informational version 3.0.0+8a13b12e799bb70a989eacac0c1b4cea22ab80ae. MVIDs: SDK 042ee775-d909-40d7-a656-06a3582b1a7e; Abstractions 268126d8-9442-4073-ad69-01def3f4b2bc.

## What this adds, and what it does not

Existing project-reference SdkTypeForwardingTests already checked 20 names and simple-name destination ownership. Existing API extraction also reflects over assemblies for public declarations and reads forwarding metadata; it is not wholly metadata-only. The new result binds explicit forwarding-hop checks to these freshly packed two package DLLs and exact full target identities.

The SDK-qualified Type.GetType entrance uses controlled assembly/type resolvers; its type resolver delegates to SDK.GetType. Thus these are three exercised entrances (Assembly.GetType, GetForwardedTypes, controlled qualified Type.GetType), not three independent binder implementations. The ALC is collectible and disposal requests unload; actual collection/unload completion is not measured. It is not a hostile-code sandbox.

A normal C# consumer compiled through the SDK facade may emit a direct Abstractions reference. This probe is **not** an ordinary PackageReference consumer or a real precompiled 2.0.3 binary. It does not establish member-level ABI compatibility, complete NuGet dependency closure, the nine-package aggregate gate, full default PackageSmoke, Trim/AOT, performance or security approval. Generator was built for SDK packaging, but not executed by this probe. Existing a7a11c85 PackageSmoke and historical API3/API4 rejection fixtures remain separate evidence, not relabeled as 8a13 supported-consumer compatibility. #805 is not declared closed.

## Local development failures and evidence boundary

Local SDK 10.0.400 build initially found one nullable-flow warning; the final build had zero warnings/errors. The first local synthetic run returned the fixed UnexpectedFailure code. The implementer diagnosed repeated identical trusted-platform assembly identity/path entries that were not coalesced; the retained failure JSON alone does not independently prove that cause. The fix accepts only exact duplicate identity/path entries and still rejects conflicts. The first failure JSON was reconstructed from captured tool output after its local file had already been overwritten; that provenance is explicitly retained locally. No raw diagnostic logs are published.

After the fix, local synthetic tests passed 31/31, executable invalid-CLI tests passed 4/4 (exit 1, fixed JSON, empty stderr), and runner tests passed 6/6. Hosted execution independently passed 31/31 and 6/6. Local CLI tests are not presented as hosted executable CLI coverage. The retained hosted JSON was cross-checked again against the pinned self-test manifest and package DLL hashes; this is an evidence consistency check, not a second independent execution or re-read of hosted DLL bytes.
