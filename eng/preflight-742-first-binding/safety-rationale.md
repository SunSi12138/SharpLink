# First-binding Task awaiter candidate

Base: recipe `06b737df0d9aab66caed9c446a572b3a8ce4efae` plus the unchanged full G2 reconstruction patch. The index was verified as tree `398d484fb5b8ab8adb75a74db7d577d929d2dc77` before edits. Production candidate: `4cb4ce7a13b06c3a6f3213e30215b6769ed49f90`.

Only `ClientConnection.SendClientStreamAsync<T>` and `RpcSession.PumpGeneratedOutboundStreamAsync<T>` change. First-send immediate success reads `ValueTask<ResolvedSendCreditLease>.Result` once. Otherwise `AsTask()` consumes the ValueTask once; the producer awaits the returned Task. The generated producer retains a nongeneric Task for deadline arbitration and reads its lease after successful observation. `sendCreditLeaseBound` is still set only after result consumption succeeds, including the flow-control-disabled default lease. The retained-send methods and all controller validators, keys, fields, locks, windows, pools, lease snapshots and wire publication operations remain unchanged.

## First-send backing audit

- Client sized dispatch returns `SendClientStreamChunkKnownSizeAsync<T>`, an ordinary `async ValueTask<ResolvedSendCreditLease>` method.
- Client unsized dispatch returns a direct `new ValueTask<ResolvedSendCreditLease>(creditLease)` after immediate publication, or `AwaitClientPreCreditBudgetAndFlowCreditAsync`, an ordinary async ValueTask method.
- Generated sized dispatch returns `SendStreamChunkKnownSizeWithCreditLeaseAsync<T>`, an ordinary async ValueTask method.
- Generated unsized dispatch returns a direct lease result after immediate publication, or `AwaitPreCreditBudgetAndResolvedFlowCreditAsync`, an ordinary async ValueTask method.
- No first-send wrapper or reachable lease-producing async method has an `AsyncMethodBuilder` override. No custom ValueTask alias or result type changes its builder. The unrelated pooled dispatcher `ReadAsync` and source-backed enumerator waits do not return first-send lease ValueTasks.
- The nested lease acquire helpers also return direct results or ordinary async ValueTasks. They do not expose an IValueTaskSource-backed lease result through these first-send methods.

The .NET 10.0.12 [ValueTask source](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/ValueTask.cs) returns the existing Task when its object is Task-backed; immediate-result conversion instead calls Task.FromResult. The [default generic async ValueTask builder](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/AsyncValueTaskMethodBuilderT.cs) supplies inline successful results or Task-backed operations, including synchronous faults and cancellations. This establishes the source contract, while conversion-allocation and identity tests remain required remote evidence.

## Outcomes and races

- Immediate success never calls AsTask, avoiding possible Task.FromResult allocation for the 40-byte lease.
- Completed fault/cancellation is not `IsCompletedSuccessfully`; AsTask returns its existing faulted/canceled Task. Await preserves exception/cancellation identity. A synchronous throw before a helper returns is untouched.
- A false completion check followed by success/fault/cancellation before AsTask is safe: a pending default-builder ValueTask already stores its Task, and completion does not replace that object. There is one ValueTask consumption. Awaiting and reading the resulting Task more than once is supported.
- No deadline: generated first-send now awaits the Task; faults/cancellation exit before lease binding. Disposal and linked lifetime cancellation remain in the same finally block.
- With a deadline: only non-successful first sends enter the existing SharpLinkTimer.WaitAsync branch. Timer false still marks deadlineWon, cancels the lifetime, observes abandoned send completion, and throws DeadlineExceeded. Timer true has already observed success before GetResult. Fault/cancellation precedence, monotonic re-arbitration, observer and disposal behavior remain unchanged.

## Verification limits and required remote evidence

The frozen tests-only overlay contains 58 cases in `FirstBindingStreamPumpTests` (expected full-suite population: 2186). It runs the real client/generated pumps with sized and unsized codecs, controls the first credit wait, checks single consumption of reusable enumerator/disposal sources and exact item/snapshot ownership, preserves exception and cancellation-token identity, and checks deadline precedence against both already-completed and later first-send failures. Actual first-send helper probes inspect Task backing, then measure one AsTask conversion with setup and reflection outside the allocation interval; pending and completion-before-conversion success/fault/cancellation cases compare returned Task identity. Immediate unsized failures throw before returning a ValueTask; exact-sized failures cover already-faulted/canceled returned ValueTasks. These are prepared tests, not locally executed evidence.

Candidate plus tests tree: `19f7089b4984d7c14fbf3bf2a84bb973cd96cb88`. Original G2 plus the identical tests tree: `003b74dd145b2263bd505c814a67f3c203b028d2`. Both overlays and the production delta were re-applied through isolated indices and their resulting trees verified. `author-manifest.json` records exact patch hashes, lengths and each filtered test population; `freeze_candidate.py` reproduces those identities without modifying the source checkout's main index.

The patch is whitespace-clean and changes only the two named producer roots. No local C# compilation or execution was performed because dotnet is absent. Project-boundary and maintainability guards both require dotnet and are recorded as blocked, not passed.

Production baseline roots initialized 256 client bytes and 304 generated-server bytes in the existing capture (80 bytes above dev's 176/224). This candidate removes the source-level large first-binding awaiter, but the actual compiled field inventory, root initialization decrease and zero AsTask conversion allocation are acceptance gates for the GitHub-only verification. No layout or allocation improvement is claimed from source alone. Existing capture is Tier1 with Synthesized PGO for the roots; it is not evidence of Dynamic PGO. The byte[] codec path is unsized. Failed validator V is excluded.

## Timer ownership correction after the first safety run

Run 37841272536 stopped in the unchanged G2 control: 2156/2186 cases passed, with 30 failures in new timer assertions. The fixture constructs a ClientConnection whose PendingDeadlineScheduler owns one infinite/disarmed timer; ManualTimeProvider counts that timer. The original assertions incorrectly expected the entire clock to drain while the connection remained alive. Candidate execution and later gates were not reached.

The corrected tests preserve all 58 cases and verify explicit ownership: zero timers before creating the connection, exactly one disarmed scheduler timer afterward, one additional timer during a pending generated first-send deadline, and return to the known one-timer disarmed baseline after pump cleanup. The abandoned-send cleanup wait is bounded internally and cannot leave a polling task running after timeout. Full clock drain and a zero-timer assertion occur after connection disposal. No production source or other tests changed. The revised tests still require remote execution.
