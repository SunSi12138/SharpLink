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

        // Close every static group before awaiting any one owner. Service/provider
        // disposal must not race an already acquired exact binding lease.
        foreach (var registration in _registrations)
            registration.Binding?.Retire();
        foreach (var registration in _registrations)
        {
            if (registration.Binding is { } binding)
                await binding.Group.WaitForDrainAsync().ConfigureAwait(false);
        }

        List<Exception>? failures = null;
        for (var index = 0; index < _registrations.Length; index++)
        {
            try
            {
                await _registrations[index].DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        try
        {
            if (_ownedServiceProvider is not null)
                await _ownedServiceProvider.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null)
            throw new AggregateException(failures);
    }
}
