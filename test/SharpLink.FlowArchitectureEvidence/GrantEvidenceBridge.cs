using System;
using System.Globalization;
using System.Text;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

// This adapter is the same measurement source in both builds. The compile flag
// only makes the candidate's new internal diagnostics API available to the harness;
// it cannot enable/disable the production algorithm or change an RPC workload.
internal static class GrantEvidenceBridge
{
#if FLOW_EVIDENCE_B2
    internal static bool Enabled => true;
#else
    internal static bool Enabled => false;
#endif

    internal readonly record struct Snapshot(
        long ActivatedStreams, long DisposedStreams, long FirstProbeFallbacks,
        long LocalItems, long OwnerRefills, long ExactSlowItems,
        long RecalledGrantBytes, long RefundedItemBytes);

    internal static Snapshot Capture()
    {
#if FLOW_EVIDENCE_B2
        var value = StreamFlowController.ReadSendGrantEvidence();
        return new Snapshot(value.ActivatedStreams, value.DisposedStreams,
            value.FirstProbeFallbacks, value.LocalItems, value.OwnerRefills,
            value.ExactSlowItems, value.RecalledGrantBytes, value.RefundedItemBytes);
#else
        return default;
#endif
    }

    internal static void RequireActivation(Options options, Snapshot before, Snapshot after)
    {
        if (after.ActivatedStreams != after.DisposedStreams ||
            before.ActivatedStreams != before.DisposedStreams)
            throw new InvalidOperationException("B2 grant handles did not settle at the measured boundary.");
        var activated = after.ActivatedStreams - before.ActivatedStreams;
        if (!options.UsesSizedDto || options.Shape is "Unary" or "Client")
        {
            if (activated != 0)
                throw new InvalidOperationException("B2 activated outside the declared sized server-output scope.");
            return;
        }
        if (Enabled && options.Items > 0 && activated <= 0)
            throw new InvalidOperationException("Candidate performed no B2 first-item admissions; silent fallback rejected.");
        if (Enabled && options.Items > 1 && options.PayloadBytes <= 64 &&
            after.LocalItems <= before.LocalItems)
            throw new InvalidOperationException("Candidate performed no local grant consumption for small long streams.");
    }

    internal static void Append(StringBuilder builder, Snapshot before, Snapshot after)
    {
        Add(builder, "b2ActivatedStreams", after.ActivatedStreams - before.ActivatedStreams);
        Add(builder, "b2DisposedStreams", after.DisposedStreams - before.DisposedStreams);
        Add(builder, "b2FirstProbeFallbacks", after.FirstProbeFallbacks - before.FirstProbeFallbacks);
        Add(builder, "b2LocalItems", after.LocalItems - before.LocalItems);
        Add(builder, "b2OwnerRefills", after.OwnerRefills - before.OwnerRefills);
        Add(builder, "b2ExactSlowAttempts", after.ExactSlowItems - before.ExactSlowItems);
        Add(builder, "b2RecalledGrantBytes", after.RecalledGrantBytes - before.RecalledGrantBytes);
        Add(builder, "b2RefundedItemBytes", after.RefundedItemBytes - before.RefundedItemBytes);
        Add(builder, "b2LiveHandlesAfterSettlement", after.ActivatedStreams - after.DisposedStreams);
        builder.AppendLine("b2CounterScope=per-context-local-writes-and-once-per-stream-aggregation-included-in-candidate-cost");
        builder.AppendLine("b2ExactSlowUnit=admission-attempt-not-successful-wire-item");
        builder.AppendLine("b2QueueOrWriterChange=false");
        builder.AppendLine("b2UnusedCreditPeak=not-instrumented-global-per-item-counter-deliberately-absent");
        builder.AppendLine("b2UnusedCreditBound=min(connection-window,active-grant-streams-times-4096)");
    }

    private static void Add(StringBuilder builder, string name, long value)
        => builder.Append(name).Append('=').AppendLine(value.ToString(CultureInfo.InvariantCulture));
}
