using System.Runtime.CompilerServices;
using SharpLink.Runtime;
#if FLOW_EVIDENCE_B2
using Lease = SharpLink.Runtime.StreamFlowController.SendCreditLease;
#else
using Lease = System.Object;
#endif

namespace SharpLink.Benchmarks;

internal static partial class Program
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunRound(int count, int round)
    {
        var before = ReadProof();
        var controllers = new StreamFlowController[count];
        var legacy = new StreamFlowController[count];
        var locks = new Lock[count];
        var leases = new Lease?[count];
        var legacyLeases = new Lease?[count];
        var poolCount = Math.Min(count, PoolControllers);
        var pooled = new StreamFlowController[poolCount];
        var pooledLeases = new Lease?[poolCount * PoolStreams];
        var identities = new object?[poolCount * PoolStreams];
        for (var i = 0; i < count; i++) legacy[i] = NewController();
        for (var i = 0; i < poolCount; i++) pooled[i] = NewController();

        Measure("lock-object-constructor", round, count, () =>
        {
            for (var i = 0; i < count; i++) locks[i] = new Lock();
        });
        Measure("lock-first-enter-exit", round, count, () =>
        {
            for (var i = 0; i < count; i++) { locks[i].Enter(); locks[i].Exit(); }
        });
        Measure("controller-constructor", round, count, () =>
        {
            for (var i = 0; i < count; i++) controllers[i] = NewController();
        });
        Measure("legacy-pristine-first-state-exact-item", round, count, () =>
        {
            for (var i = 0; i < count; i++) KeyedProbe(legacy[i], 1);
        });
        Measure("legacy-pristine-second-exact-item", round, count, () =>
        {
            for (var i = 0; i < count; i++) KeyedAcquire(legacy[i], 1);
        });
        Measure("legacy-pristine-refund-and-complete", round, count, () =>
        {
            for (var i = 0; i < count; i++)
            {
                legacy[i].ReturnUnsentCredit(1, 0, 2 * ItemBytes);
                legacy[i].CompleteSendStream(1, 0);
            }
        });
        foreach (var controller in legacy) AssertSettled(controller);
        Measure("legacy-pristine-pooled-reuse", round, count, () =>
        {
            for (var i = 0; i < count; i++) KeyedProbe(legacy[i], 2);
        });
        foreach (var controller in legacy)
        {
            controller.ReturnUnsentCredit(2, 0, ItemBytes);
            controller.CompleteSendStream(2, 0);
            AssertSettled(controller);
        }
        Measure("first-lease-on-pristine-pooled-state", round, count, () =>
        {
            for (var i = 0; i < count; i++) legacyLeases[i] = Capture(legacy[i], 3);
        });
        Measure("pristine-pool-lease-refund-dispose-complete", round, count, () =>
        {
            for (var i = 0; i < count; i++)
            {
                Refund(legacy[i], legacyLeases[i], 3, ItemBytes);
                DisposeLease(legacyLeases[i]);
                CompleteLease(legacy[i], legacyLeases[i], 3);
            }
        });
        foreach (var controller in legacy)
        {
            AssertSettled(controller);
            controller.Complete(Terminal);
        }

        Measure("first-state-and-first-lease-exact-item", round, count, () =>
        {
            for (var i = 0; i < count; i++) leases[i] = Capture(controllers[i], 1);
        });
        foreach (var controller in controllers) AssertLive(controller, 1, ItemBytes, 0);
        Measure("second-item-owner-refill", round, count, () =>
        {
            for (var i = 0; i < count; i++) Acquire(controllers[i], leases[i], 1);
        });
        foreach (var controller in controllers) AssertLive(controller, 1, 2 * ItemBytes, GrantQuantum - ItemBytes);
        Measure("third-item-local-grant", round, count, () =>
        {
            for (var i = 0; i < count; i++) Acquire(controllers[i], leases[i], 1);
        });
        foreach (var controller in controllers) AssertLive(controller, 1, 3 * ItemBytes, GrantQuantum - 2 * ItemBytes);
        Measure("window-update-first-item", round, count, () =>
        {
            for (var i = 0; i < count; i++) controllers[i].ApplyWindowUpdate(1, 0, ItemBytes);
        });
        Measure("return-unsent-one-item", round, count, () =>
        {
            for (var i = 0; i < count; i++) Refund(controllers[i], leases[i], 1, ItemBytes);
        });
        foreach (var controller in controllers) AssertLive(controller, 1, ItemBytes, GrantQuantum - 2 * ItemBytes);
        Measure("disable-unused-grant", round, count, () =>
        {
            for (var i = 0; i < count; i++) Disable(leases[i]);
        });
        foreach (var controller in controllers) AssertLive(controller, 1, ItemBytes, 0, enabled: false);
        Measure("post-disable-exact-item", round, count, () =>
        {
            for (var i = 0; i < count; i++) Probe(controllers[i], leases[i], 1);
        });
        Measure("remaining-item-refund-before-dispose", round, count, () =>
        {
            for (var i = 0; i < count; i++) Refund(controllers[i], leases[i], 1, 2 * ItemBytes);
        });
        Measure("dispose-empty-grant", round, count, () =>
        {
            for (var i = 0; i < count; i++) DisposeLease(leases[i]);
        });
        foreach (var controller in controllers) AssertLive(controller, 1, 0, 0, enabled: false, closed: true);
        Measure("complete-and-retain-one-state", round, count, () =>
        {
            for (var i = 0; i < count; i++) CompleteLease(controllers[i], leases[i], 1);
        });
        foreach (var controller in controllers) AssertSettled(controller);

        Measure("lease-on-reused-state-with-existing-lock", round, count, () =>
        {
            for (var i = 0; i < count; i++) leases[i] = Capture(controllers[i], 2);
        });
        Measure("reused-state-second-item-refill", round, count, () =>
        {
            for (var i = 0; i < count; i++) Acquire(controllers[i], leases[i], 2);
        });
        foreach (var controller in controllers) AssertLive(controller, 2, 2 * ItemBytes, GrantQuantum - ItemBytes);
        Measure("dispose-unused-grant", round, count, () =>
        {
            for (var i = 0; i < count; i++) DisposeLease(leases[i]);
        });
        foreach (var controller in controllers) AssertLive(controller, 2, 2 * ItemBytes, 0, enabled: false, closed: true);
        Measure("late-owned-item-refund-after-dispose", round, count, () =>
        {
            for (var i = 0; i < count; i++) Refund(controllers[i], leases[i], 2, 2 * ItemBytes);
        });
        Measure("complete-reused-state", round, count, () =>
        {
            for (var i = 0; i < count; i++) CompleteLease(controllers[i], leases[i], 2);
        });
        foreach (var controller in controllers) AssertSettled(controller);
        Measure("legacy-reuse-after-leased-state", round, count, () =>
        {
            for (var i = 0; i < count; i++) KeyedProbe(controllers[i], 3);
        });
        Measure("legacy-second-item-after-leased-state", round, count, () =>
        {
            for (var i = 0; i < count; i++) KeyedAcquire(controllers[i], 3);
        });
        Measure("legacy-after-lease-refund-and-complete", round, count, () =>
        {
            for (var i = 0; i < count; i++)
            {
                controllers[i].ReturnUnsentCredit(3, 0, 2 * ItemBytes);
                controllers[i].CompleteSendStream(3, 0);
            }
        });
        foreach (var controller in controllers) AssertSettled(controller);
        Measure("connection-complete-clear-pool", round, count, () =>
        {
            for (var i = 0; i < count; i++) controllers[i].Complete(Terminal);
        });

        Measure("pool128-first-publish-before-retirement", round, poolCount, () =>
        {
            for (var i = 0; i < poolCount; i++)
                for (ushort sid = 0; sid < PoolStreams; sid++)
                    pooledLeases[i * PoolStreams + sid] = Capture(pooled[i], 1, sid);
        });
        for (var i = 0; i < poolCount; i++)
        {
            Require(pooled[i].ActiveSendStreamCount == PoolStreams && pooled[i].RetainedSendStreamCount == PoolStreams,
                "pool setup must have 128 simultaneous live streams before any retirement");
#if FLOW_EVIDENCE_B2
            for (ushort sid = 0; sid < PoolStreams; sid++)
                identities[i * PoolStreams + sid] = pooled[i].CaptureSendGrantSnapshot(1, sid).StateIdentity;
#endif
        }
        Measure("pool128-refund-dispose-complete", round, poolCount, () =>
        {
            for (var i = 0; i < poolCount; i++)
                for (ushort sid = 0; sid < PoolStreams; sid++)
                {
                    var lease = pooledLeases[i * PoolStreams + sid];
                    Refund(pooled[i], lease, 1, ItemBytes, sid);
                    DisposeLease(lease);
                    CompleteLease(pooled[i], lease, 1, sid);
                }
        });
        foreach (var controller in pooled) AssertSettled(controller);
        Measure("pool128-recapture-reusing-states-and-locks", round, poolCount, () =>
        {
            for (var i = 0; i < poolCount; i++)
                for (ushort sid = 0; sid < PoolStreams; sid++)
                    pooledLeases[i * PoolStreams + sid] = Capture(pooled[i], 2, sid);
        });
#if FLOW_EVIDENCE_B2
        for (var i = 0; i < poolCount; i++)
            for (ushort sid = 0; sid < PoolStreams; sid++)
            {
                var state = pooled[i].CaptureSendGrantSnapshot(2, sid).StateIdentity;
                Require(state is not null && ReferenceEquals(state, identities[i * PoolStreams + PoolStreams - 1 - sid]),
                    "candidate did not reuse each of its 128 physical pooled states in LIFO order");
            }
#endif
        Measure("pool128-second-refund-dispose-complete", round, poolCount, () =>
        {
            for (var i = 0; i < poolCount; i++)
                for (ushort sid = 0; sid < PoolStreams; sid++)
                {
                    var lease = pooledLeases[i * PoolStreams + sid];
                    Refund(pooled[i], lease, 2, ItemBytes, sid);
                    DisposeLease(lease);
                    CompleteLease(pooled[i], lease, 2, sid);
                }
        });
        foreach (var controller in pooled)
        {
            AssertSettled(controller);
            controller.Complete(Terminal);
        }
        VerifyProof(ReadProof() - before, count, poolCount, round);
        GC.KeepAlive(controllers);
        GC.KeepAlive(legacy);
        GC.KeepAlive(locks);
        GC.KeepAlive(leases);
        GC.KeepAlive(legacyLeases);
        GC.KeepAlive(pooled);
        GC.KeepAlive(pooledLeases);
        GC.KeepAlive(identities);
    }

    private static Lease? Capture(StreamFlowController controller, long requestId, ushort sid = 0)
    {
#if FLOW_EVIDENCE_B2
        Require(controller.TryAcquireSendCreditLease(requestId, sid, ItemBytes, CancellationToken.None, out var lease) && lease is not null,
            "the actual B2 production lease path was not activated");
        return lease;
#else
        Require(controller.TryAcquireSendCredit(requestId, sid, ItemBytes), "baseline exact acquisition failed");
        return null;
#endif
    }

    private static void KeyedProbe(StreamFlowController controller, long requestId)
        => Require(controller.TryAcquireSendCredit(requestId, 0, ItemBytes), "keyed probe unexpectedly blocked");

    private static void KeyedAcquire(StreamFlowController controller, long requestId)
        => Synchronous(controller.AcquireSendCreditAsync(requestId, 0, ItemBytes, CancellationToken.None));

    private static void Acquire(StreamFlowController controller, Lease? lease, long requestId)
    {
#if FLOW_EVIDENCE_B2
        Synchronous(lease!.AcquireAsync(ItemBytes, CancellationToken.None));
#else
        KeyedAcquire(controller, requestId);
#endif
    }

    private static void Synchronous(ValueTask operation)
    {
        Require(operation.IsCompletedSuccessfully, "allocation phases must not enqueue, block or await");
        operation.GetAwaiter().GetResult();
    }

    private static void Probe(StreamFlowController controller, Lease? lease, long requestId)
    {
#if FLOW_EVIDENCE_B2
        Require(lease!.TryAcquire(ItemBytes, CancellationToken.None), "same-lease exact probe failed after disable");
#else
        KeyedProbe(controller, requestId);
#endif
    }

    private static void Refund(StreamFlowController controller, Lease? lease, long requestId, int bytes, ushort sid = 0)
    {
#if FLOW_EVIDENCE_B2
        lease!.ReturnUnsent(bytes);
#else
        controller.ReturnUnsentCredit(requestId, sid, bytes);
#endif
    }

    private static void Disable(Lease? lease)
    {
#if FLOW_EVIDENCE_B2
        lease!.DisableGrant();
#endif
    }

    private static void DisposeLease(Lease? lease)
    {
#if FLOW_EVIDENCE_B2
        lease!.Dispose();
#endif
    }

    private static void CompleteLease(StreamFlowController controller, Lease? lease, long requestId, ushort sid = 0)
    {
#if FLOW_EVIDENCE_B2
        lease!.Complete();
#else
        controller.CompleteSendStream(requestId, sid);
#endif
    }

    private static void AssertLive(StreamFlowController controller, long requestId, int debt, int grant, bool enabled = true, bool closed = false)
    {
#if FLOW_EVIDENCE_B2
        var snapshot = controller.CaptureSendGrantSnapshot(requestId, 0);
        Require(snapshot.HasState && snapshot.CurrentLease is not null && snapshot.WaiterCount == 0 &&
                snapshot.UnusedGrant == grant && snapshot.TotalUnusedGrant == grant && snapshot.TotalDebt == debt &&
                snapshot.StreamCredit == Defaults.FlowControl.StreamReceiveWindowBytes - debt &&
                snapshot.ConnectionCredit == Defaults.FlowControl.ConnectionReceiveWindowBytes - debt - grant &&
                snapshot.GrantEnabled == enabled && snapshot.LeaseClosed == closed, "candidate phase credit/ownership proof failed");
#else
        Require(controller.SendConnectionCredit == Defaults.FlowControl.ConnectionReceiveWindowBytes - debt &&
                controller.ActiveSendStreamCount == 1 && controller.RetainedSendStreamCount == 1,
                "baseline exact phase credit proof failed");
#endif
    }

    private readonly record struct Proof(long ActivatedStreams, long DisposedStreams, long FirstProbeFallbacks,
        long LocalItems, long OwnerRefills, long ExactSlowItems, long RecalledGrantBytes, long RefundedItemBytes)
    {
        public static Proof operator -(Proof a, Proof b) => new(a.ActivatedStreams - b.ActivatedStreams,
            a.DisposedStreams - b.DisposedStreams, a.FirstProbeFallbacks - b.FirstProbeFallbacks,
            a.LocalItems - b.LocalItems, a.OwnerRefills - b.OwnerRefills, a.ExactSlowItems - b.ExactSlowItems,
            a.RecalledGrantBytes - b.RecalledGrantBytes, a.RefundedItemBytes - b.RefundedItemBytes);
    }

    private static Proof ReadProof()
    {
#if FLOW_EVIDENCE_B2
        var e = StreamFlowController.ReadSendGrantEvidence();
        return new(e.ActivatedStreams, e.DisposedStreams, e.FirstProbeFallbacks, e.LocalItems,
            e.OwnerRefills, e.ExactSlowItems, e.RecalledGrantBytes, e.RefundedItemBytes);
#else
        return default;
#endif
    }

    private static void VerifyProof(Proof actual, int count, int poolCount, int round)
    {
#if FLOW_EVIDENCE_B2
        var streams = 3L * count + 2L * poolCount * PoolStreams;
        var expected = new Proof(streams, streams, 0, count, 2L * count, count,
            (2L * GrantQuantum - 3L * ItemBytes) * count,
            6L * ItemBytes * count + 2L * poolCount * PoolStreams * ItemBytes);
#else
        Proof expected = default;
#endif
        if (actual != expected)
            throw new InvalidOperationException($"Actual production counters differ from the workload: actual={actual}; expected={expected}");
        if (round < 0) return;
        var prefix = "round." + round + ".";
        Metadata(prefix + nameof(Proof.ActivatedStreams), actual.ActivatedStreams.ToString());
        Metadata(prefix + nameof(Proof.DisposedStreams), actual.DisposedStreams.ToString());
        Metadata(prefix + nameof(Proof.FirstProbeFallbacks), actual.FirstProbeFallbacks.ToString());
        Metadata(prefix + nameof(Proof.LocalItems), actual.LocalItems.ToString());
        Metadata(prefix + nameof(Proof.OwnerRefills), actual.OwnerRefills.ToString());
        Metadata(prefix + nameof(Proof.ExactSlowItems), actual.ExactSlowItems.ToString());
        Metadata(prefix + nameof(Proof.RecalledGrantBytes), actual.RecalledGrantBytes.ToString());
        Metadata(prefix + nameof(Proof.RefundedItemBytes), actual.RefundedItemBytes.ToString());
    }
}
