# Stable TCP evidence versus default-socket diagnostics

The default loopback TCP profile remains in the evidence suite and is never retried or overwritten. Hosted Linux receive-window autotuning can enter a slow-progress state even when application send queues are empty.

Performance acceptance may therefore use the unchanged 48-process/768-sample A-ready/B3 matrix with one benchmark-only control: TCP endpoints use an explicit 262144-byte receive buffer in the stable JIT gate. SharedMemory, flow-control windows, SendPump, items, byte budgets and quanta are unchanged. The original default-socket population remains a diagnostic observation and its failures remain artifacts.

The knob is compiled only in the ReadyWriter benchmark experiment. No shipping transport default is changed. Provenance and report metadata record the requested buffer so default and controlled data cannot be pooled.
