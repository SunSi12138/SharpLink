using TUnit.Core;

// Real socket and pipe tests intentionally share process-wide registries while the
// instance-scoped runtime context is introduced in 0.5.1. Keep this suite deterministic
// until those globals are removed.
[assembly: NotInParallel]
