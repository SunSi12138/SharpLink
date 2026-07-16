namespace SharpLink.Runtime;

/// <summary>Contains the inheritable handles needed to connect an anonymous-pipe client.</summary>
public readonly record struct AnonymousPipeOffer(string InHandle, string OutHandle);
