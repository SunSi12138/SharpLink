namespace SharpLink.Runtime;

/// <summary>Configures one named-pipe transport endpoint.</summary>
public sealed class NamedPipeTransportOptions
{
    /// <summary>
    /// Gets or sets whether the named pipe may be accessed by other local users.
    /// When false (the default), the pipe is created with
    /// <see cref="System.IO.Pipes.PipeOptions.CurrentUserOnly"/>.
    /// </summary>
    public bool AllowCrossUserAccess { get; set; }

    internal System.IO.Pipes.PipeOptions ToPipeOptions()
        => System.IO.Pipes.PipeOptions.Asynchronous |
           (AllowCrossUserAccess
               ? System.IO.Pipes.PipeOptions.None
               : System.IO.Pipes.PipeOptions.CurrentUserOnly);
}
