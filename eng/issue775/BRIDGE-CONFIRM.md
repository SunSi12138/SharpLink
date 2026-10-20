# #775: fixed bridge independent confirmation (2026-10-20, JST)

## Predeclared question and frozen sources

Discovery run 39299971567 found favorable OneWay c8/p4 receiver throughput versus old
static, but unfavorable SharedMemory Unary c8/p4 throughput/CPU although Unary source
was not edited. Neither result grants original-baseline recovery or phase-2 approval.
Do not stack another optimization or discard that discovery run.

Exactly three new Actions VM jobs, one cohort, first run attempt only. Each rebuilds the
same old source `6cfb09e7dd6b68695f92d28f726340bc8eb39e1d` and the same bridge candidate
`8fc84d486c242d67fe05d4cf9962661d8351c779`, tree
`1bd11d1d56ef951428440ee5cede21af94d1c80e`, from frozen experiment
`5487a21f020e16c2b036b439b8d7efd896267422` and its SHA256-locked bridge.patch.
No source optimization, detached-site patch, new ABI, receipt, streaming feature or
production integration belongs to this confirmation. Current remote src remains unchanged.

Each VM executes the unchanged pinned boundary_pair runner: ABBA+BAAB, eight slots,
TCP/SharedMemory x Unary/OneWay x c1/p1,c8/p4; 10,000 warmup and 100,000 measured RPCs,
two CPUs, original tiering/PGO. Original BDN method/labels and 3 launches x 6 warmup x
15 actual iterations / 4096 invocations remain unchanged. Four original allocation
cases per slot, then +512 B/op negative control with the original budgets. No runtime
instrumentation, test shim or closed-loop diagnostic workload enters paired timing.
Do not stop early for favorable or unfavorable performance and do not rerun a VM to
select samples. Infrastructure/execution failures remain failures with raw output.

This cohort compares ONLY with old static. The original-dev discovery comparison and
all earlier unfavorable baseline results remain intact; this cohort cannot certify
baseline recovery even when old-static replication looks favorable.

## Same-binary control and isolated code generation

After all eight paired slots and the allocation negative control, each VM runs
XYYXYXXY using precisely the same old directory and DLLs for X and Y. No second build.
Three focus cases: TCP OneWay c8/p4, SharedMemory OneWay c8/p4, SharedMemory Unary c8/p4.
Both X and Y use 10,000/100,000; raw sender/receiver values retained. Hash every DLL and
compare with the paired old build. AA tests measurement variability late in the job;
it does not measure all earlier drift, cannot be subtracted, and cannot excuse a
candidate regression. It also does not include BDN, so cannot calibrate BDN noise.

Finally, separate diagnostic processes collect TCP/SharedMemory Unary c8/p4 client
entry and server DispatchRpcAsync JIT output, 10,000/300,000 each arm. This is not
formal timing and changes no compiled source. Record complete raw listings, environment,
exit codes and actual Tier1 coverage. Missing/incomplete/interleaved Tier1 is retained,
not retried and not replaced by Tier0. Successful timing with missing JIT coverage is
not a completed root-cause explanation. No CPU percentage is inferred from code size.

## Predeclared statistical analysis

Four primary endpoints versus old static: TCP and SharedMemory OneWay c8/p4 receiver
throughput, SharedMemory Unary c8/p4 receiver throughput, and its process CPU time for
the fixed completed work. Both sender BDN labels, tail latencies and all other metrics
are retained as secondary/exploratory; labels are the same real OneWay expression.

Show all four adjacent-pair log ratios on each VM, each VM's geometric effect and
exploratory 95% log-t interval (df=3). For the cohort, average the four log ratios within
each VM, then give equal weight to the THREE host averages: log-t interval df=2. Do
not treat twelve pairs, launches or individual latencies as independent hosts.
Two-sided host-level p values for the four primary endpoints get Holm adjustment.
All other intervals are exploratory with no multiplicity claim. Report direction
consistency and AA beside candidate results even when nonsignificant. Host effects
must be approximately independent/normal; three VMs remain a small sample. Independent
VM jobs do not establish distinct physical hosts. Record boot/runner/image identities.

No approved equivalence margin exists; nonsignificance is not equality. No arbitrary
3% gate. Never pool absolute throughput across VMs or pool the discovery run into the
confirmation cohort. Hypothesis supported, contradicted or unresolved is distinct from
production/phase-2 authorization, which remains false.

Expected paired totals: 192 real-RPC executions, 2,160 BDN actual measurements, 96
allocation cases, three negative controls. AA adds 72 real-RPC executions; paired+AA
retain 52,800,000 sender/receiver latency values. Twelve separate Unary JIT executions
are excluded from those totals. Audit all three artifacts before cohort conclusions.

Offline audit using this driver's scripts (no dotnet needed):

```sh
python3 -m unittest discover -s eng/issue775 -p test_bridge_confirm.py -v
python3 eng/issue775/bridge_confirm_report.py vm1 vm2 vm3 --output report
```

PR #862 stays Draft and #775 open; Replace/Receipt/Streaming are not authorized.
