using System;
using SharpLink.StreamCodecStructCoreEvidence;

#if GENERATED_BINDING_PAIRED
GeneratedCodecBindingEvidenceRunner.RunAotPaired(args);
#elif GENERATED_BINDING_INTERFACE || GENERATED_BINDING_GUARDED_VALUE || GENERATED_BINDING_GUARDED_REF || GENERATED_BINDING_DIRECT
GeneratedCodecBindingEvidenceRunner.Run(args);
#else
if (args.Length > 0 && string.Equals(args[0], "--generated-binding", StringComparison.Ordinal))
    GeneratedCodecBindingEvidenceRunner.Run(args[1..]);
else
    StructCoreEvidenceRunner.Run(args);
#endif
