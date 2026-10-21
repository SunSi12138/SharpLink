These are separate JIT diagnostics, not performance samples.
Both used SDK 10.0.102 / runtime 10.0.2 with tiered compilation disabled.
ReadyToRun was left at its environment default. Actual captured bodies are FullOpts.
Run bash reproduce.sh with DOTNET, BASELINE, CANDIDATE and OUTPUT overridden as needed.
The script contains the exact diagnostic workload and reconstructs the archived layout probe.
Only the four relevant complete method bodies are checked in; broad 2 MB assembly dumps are omitted.
The summary records actual generated names, native code bytes and stack reservation instructions.
The first reservation is the main body; later reservations include exception funclets.
The layout probe loads each binary set in a separate process and invokes Unsafe.SizeOf<T>() on the
actual closed generic OneWay state machine. This measures managed values, not rounded boxed allocations.
Descriptor: 32 to 40 bytes. OneWay state machine: 192 to 200 bytes.
Unary native code: 952 to 972 bytes. OneWay MoveNext: 2480 to 2531 bytes.
Main stack reservations: 264 / 480 bytes, unchanged. Wider descriptor copies exist on the no-observer
path. Native code size alone does not quantify runtime slowdown.
