namespace SharpLink.Hosting;

/// <summary>Exposes the in-process SharpLink server readiness state to host integrations.</summary>
public interface ISharpLinkServerReadiness
{
    /// <summary>Gets the current local server readiness state.</summary>
    SharpLinkHealthStatus Status { get; }
}

internal sealed class SharpLinkServerReadiness : ISharpLinkServerReadiness
{
    private ISharpLinkServer? _server;

    public SharpLinkHealthStatus Status =>
        Volatile.Read(ref _server)?.HealthStatus ?? SharpLinkHealthStatus.Unhealthy;

    internal void Publish(ISharpLinkServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        Volatile.Write(ref _server, server);
    }

    internal void Clear(ISharpLinkServer server)
        => Interlocked.CompareExchange(ref _server, null, server);
}

/// <summary>Maps the local SharpLink server state into Microsoft.Extensions.Diagnostics health checks.</summary>
/// <param name="readiness">The local server readiness source.</param>
public sealed class SharpLinkServerHealthCheck(ISharpLinkServerReadiness readiness) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = readiness.Status switch
        {
            SharpLinkHealthStatus.Ready => HealthCheckResult.Healthy("SharpLink server is ready."),
            SharpLinkHealthStatus.Draining => HealthCheckResult.Degraded("SharpLink server is draining."),
            _ => HealthCheckResult.Unhealthy("SharpLink server is not ready.")
        };
        return Task.FromResult(result);
    }
}

/// <summary>Queries a connected SharpLink server through the protocol health control frame.</summary>
/// <param name="clientAccessor">Provides a client only after at least one connection is ready.</param>
public sealed class SharpLinkRemoteHealthCheck(ISharpLinkClientAccessor clientAccessor) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await clientAccessor.GetClientAsync(cancellationToken).ConfigureAwait(false);
            var response = await client.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
            return response.Status switch
            {
                SharpLinkHealthStatus.Ready => HealthCheckResult.Healthy("Remote SharpLink server is ready."),
                SharpLinkHealthStatus.Draining => HealthCheckResult.Degraded("Remote SharpLink server is draining."),
                _ => HealthCheckResult.Unhealthy("Remote SharpLink server is unhealthy.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                "Remote SharpLink health check failed.",
                exception);
        }
    }
}
