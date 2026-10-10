"""Fixed counter schema, shared by projection, validation, and reporting."""
NAMES = [
    'logical_decision', 'logical_fast', 'logical_slow', 'logical_helper_entry',
    'operation_registration_entry', 'operation_registration_accepted',
    'operation_registration_failure', 'operation_registration_inflight',
    'operation_new', 'permit_plain_new', 'permit_decode_new',
    'context_cache_hit', 'context_snapshot_new',
    'push_null_null', 'push_null_snapshot', 'push_same_snapshot',
    'push_snapshot_null', 'push_different_snapshot',
    'restore_null_null', 'restore_null_snapshot', 'restore_same_snapshot',
    'restore_snapshot_null', 'restore_different_snapshot',
    'ownership_read_calls', 'ownership_read_completed_successfully',
    'ownership_read_other', 'ownership_helper_entry',
    'send_accepted_frames', 'send_flush_calls', 'send_flush_frames',
    'send_flush_completed_at_probe', 'send_flush_incomplete_at_probe',
    'send_capacity_tcs_new', 'send_capacity_wait_uses',
    'wakeup_registration_accepted', 'shm_pulse_registration_accepted',
]

def counter_source():
    constants = '\n'.join(f'    public const int {n} = {i};' for i, n in enumerate(NAMES))
    names = ', '.join('"' + n + '"' for n in NAMES)
    return '''// Diagnostic projection only. No instance fields or per-call objects.
#pragma warning disable CS1591 // Diagnostic-only bridge; not a shipped public API.
using System;
using System.Threading;
namespace SharpLink.Abstractions;
public static class Issue739MultiplicityCounters
{
''' + constants + '''
    private static readonly long[] Values = new long[''' + str(len(NAMES)) + '''];
    private static readonly string[] Names = [''' + names + '''];
    public static string[] GetNames() => Names;
    public static void Increment(int index) => Interlocked.Increment(ref Values[index]);
    public static void Add(int index, long value) => Interlocked.Add(ref Values[index], value);
    public static long Read(int index) => Interlocked.Read(ref Values[index]);
    public static void Capture(long[] destination)
    {
        if (destination.Length != Values.Length) throw new ArgumentException("Counter length");
        for (int i = 0; i < Values.Length; i++) destination[i] = Interlocked.Read(ref Values[i]);
    }
    public static void Transition(int offset, object? before, object? after)
    {
        int category = before is null ? (after is null ? 0 : 1)
            : ReferenceEquals(before, after) ? 2 : after is null ? 3 : 4;
        Increment(offset + category);
    }
    public static void PrimitiveLoop(int count)
    {
        for (int i = 0; i < count; i++)
        {
            Increment(operation_registration_entry);
            Increment(operation_registration_inflight);
            try { Increment(operation_registration_accepted); }
            finally { Add(operation_registration_inflight, -1); }
            Transition(push_null_null, null, null);
            _ = Read(operation_registration_inflight);
        }
    }
}
'''
