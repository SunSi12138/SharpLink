namespace SharpLink.Abstractions;

/// <summary>Provides the prepared execution entries of one generated server contract.</summary>
public interface IRpcStub
{
    /// <summary>Gets the stable generated contract identifier.</summary>
    long InterfaceHash { get; }

    /// <summary>Resolves the exact method facts and execution body during registration.</summary>
    bool TryPrepareMethod(long methodId, out RpcGeneratedPreparedMethod method);
}
