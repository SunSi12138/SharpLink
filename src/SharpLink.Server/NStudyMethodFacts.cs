namespace SharpLink.Server;

// Stored with the stub and codecs of one exact registration. A call copies
// this scalar record before admission; a waiting call does not root an ALC.
internal readonly record struct NStudyMethodFacts(RpcMethodDescriptor Descriptor, bool SupportsCancellation, int ExecutorSlot);
