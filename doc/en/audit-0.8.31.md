# SharpLink 0.8.31 deep audit

Chinese: [`../audit-0.8.31.md`](../audit-0.8.31.md)

Using 0.8.30 commit `6ecdac9` as the baseline, this batch verified five P2 improvements: custom mutable socket endpoints were retained by reference; Unix listener disposal deleted a caller-owned path replacement; public raw frame tokens could backfill an unrelated writer; anonymous-pipe offers neither completed the required parent-side handle transfer nor redacted handles; and obsolete registries/interfaces/collections plus implementation helpers remained exported.

Custom endpoints now require an independent `Create(Serialize())` snapshot. Unix filesystem sockets are owned by type/device/inode through .NET's stable cross-Unix `System.Native` lstat ABI, preserving a replacement across socket disposal. Anonymous-pipe offers provide idempotent transfer completion for both handles and redact diagnostics. The raw frame body stays unchanged but becomes internal with the other packet/buffer/striped helpers; dead registries, interfaces, and set were removed.

The official [`DisposeLocalCopyOfClientHandle` contract](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.anonymouspipeserverstream.disposelocalcopyofclienthandle?view=net-9.0) requires the parent to close its local copy after transfer or the server cannot observe client disposal. The runtime [`Interop.Stat`](https://github.com/dotnet/runtime/blob/main/src/libraries/Common/src/Interop/Unix/System.Native/Interop.Stat.cs) and native [`pal_io.h`](https://github.com/dotnet/runtime/blob/main/src/native/libs/System.Native/pal_io.h) define the cross-Unix status layout, socket type, and stable ABI used here.

The complete pre-fix Unit run preserved all 468 existing passes and failed only the five new probes out of 473. After removing three tests for the deleted registries, Unit is 470/470. The final non-incremental Release build has zero warnings/errors; Generator is 102/102, Integration 237/237, and seven-package plus fresh-cache smoke pass. See [`../performance-0.8.31.md`](../performance-0.8.31.md) and [`../migration-0.8.31.md`](../migration-0.8.31.md). Consecutive clean audit rounds remain 0/3.
