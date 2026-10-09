global using System.Diagnostics;
global using System.Diagnostics.CodeAnalysis;
global using System.Runtime.CompilerServices;
global using SharpLink.Abstractions;
global using SharpLink.Runtime;

namespace SharpLink.FlowStatePhaseB;

// Test-only direct attribution. Timing runs disable timestamps; diagnostic runs
// enable them separately. Counters are protected by this gate, not shared RMWs.
internal sealed class ProbeGate
{
    private readonly Lock _gate = new();
    internal bool Diagnose;
    internal long Entries;
    internal long WaitTicks;
    internal long HoldTicks;

    internal Scope EnterScope()
    {
        var before = Diagnose ? Stopwatch.GetTimestamp() : 0;
        _gate.Enter();
        var acquired = Diagnose ? Stopwatch.GetTimestamp() : 0;
        if (Diagnose)
        {
            Entries++;
            WaitTicks += acquired - before;
        }
        return new Scope(this, acquired);
    }

    internal readonly ref struct Scope(ProbeGate owner, long acquired)
    {
        public void Dispose()
        {
            if (owner.Diagnose)
                owner.HoldTicks += Stopwatch.GetTimestamp() - acquired;
            owner._gate.Exit();
        }
    }
}
