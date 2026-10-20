# #775: fixed no-stream bridge performance plan

Frozen candidate: local commit 8fc84d486c242d67fe05d4cf9962661d8351c779,
tree 1bd11d1d56ef951428440ee5cede21af94d1c80e, built from base
6cfb09e7dd6b68695f92d28f726340bc8eb39e1d plus bridge.patch in driver
5487a21f020e16c2b036b439b8d7efd896267422. It is not a published production commit.
No detached BindingSite patch is stacked onto this candidate.

Before this plan: run 39299115699 completed default/codegen successfully. Both static
arms passed 443 smoke, 416 semantic assertions, 46 bridge assertions and 12 persistent
cases. Default candidate passed 2123 units, 271 generator tests, format and original
maintainability. TUnit report SHA is the CI driver; candidate identity/cwd/source archive
establish the actual tested bytes. Do not relabel the report SHA as the local commit.

Complete Tier1 review in four sender cases per arm: old channel uses a regular call and
return temporaries (158 code bytes); candidate uses tail.jmp to the exact no-stream
entry (87 bytes), with the shape rejection still preceding it. No forced inlining,
changed runtime knobs, API/generator changes or skip of admission/serialization guards.
The caller remains an indirect interface call. Descriptor movement and state-machine
initialization remain; smaller channel code is not a speed score.

Run exactly one complete ABBA+BAAB per comparison, each serial on its own VM: original
baseline 1a54993d3550911f5979a7b4d3f9f5f63ee86a64 and previous static 6cfb09e7.
Do not combine samples or compare absolute rates across the two VMs. Previous-static
is attribution only, never a replacement for original dev.

Each job: 8 slots, 64 true TCP/SharedMemory scenarios, 720 BDN actual iterations,
32 canonical allocation cases and +512 B/op negative control. Existing runners are
unchanged: 10000 warmup/100000 measured, c1/p1 and c8/p4, two CPUs, original tiering/PGO,
BDN 3 launches x 6 warmup x 15 actual with 4096 invocations. The two PayloadSize labels
execute the same existing OneWay expression. No diagnostics, local completion batches,
custom writer shim or semantic overlay is installed into formal measurement.

No retries to select results; retain failure/unfavorable data, allocation budgets and
negative-control boundaries. Four-pair log-t intervals (df=3) are exploratory with no
multiplicity correction or approved equivalence margin. Success of execution is not
performance recovery or phase-2 authorization. PR stays Draft pending result review.
