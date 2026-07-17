namespace SharpLink.Server;

/// <summary>
/// Owns service registrations independently from transport state so an uncooperative
/// invocation can keep only its service graph alive after the server has stopped.
/// </summary>
internal sealed class ServerServiceCleanup(
    IEnumerable<ServiceRegistration> registrations,
    IAsyncDisposable? ownedServiceProvider) : IAsyncDisposable
{
    private readonly ServiceRegistration[] _registrations = [.. registrations];
    private readonly IAsyncDisposable? _ownedServiceProvider = ownedServiceProvider;
    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Exception? firstException = null;
        for (var index = 0; index < _registrations.Length; index++)
        {
            try
            {
                await _registrations[index].DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                firstException ??= exception;
            }
        }

        try
        {
            if (_ownedServiceProvider is not null)
                await _ownedServiceProvider.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            firstException ??= exception;
        }

        if (firstException is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstException).Throw();
    }
}
