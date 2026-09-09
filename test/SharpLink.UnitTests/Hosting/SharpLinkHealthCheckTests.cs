using Microsoft.Extensions.Diagnostics.HealthChecks;
using SharpLink.Hosting;

namespace SharpLink.UnitTests.Hosting;

public class SharpLinkHealthCheckTests
{
    [Test]
    [Arguments(SharpLinkHealthStatus.Ready, HealthStatus.Healthy)]
    [Arguments(SharpLinkHealthStatus.Draining, HealthStatus.Degraded)]
    [Arguments(SharpLinkHealthStatus.Unhealthy, HealthStatus.Unhealthy)]
    public async Task LocalHealthCheckShouldMapReadiness(
        SharpLinkHealthStatus status,
        HealthStatus expected)
    {
        var check = new SharpLinkServerHealthCheck(new FixedReadiness(status));
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        if (result.Status != expected)
        {
            throw new Exception(
                $"expected health status {expected}, received {result.Status}");
        }
    }

    [Test]
    public void LocalHealthCheckShouldNotAllocateACompletedTaskPerPoll()
    {
        var check = new SharpLinkServerHealthCheck(new FixedReadiness(SharpLinkHealthStatus.Ready));
        var context = new HealthCheckContext();
        var cached = check.CheckHealthAsync(context);
        if (!cached.IsCompletedSuccessfully || cached.Result.Status != HealthStatus.Healthy)
            throw new Exception("the cached local health task must complete synchronously as Healthy");

        for (var index = 0; index < 100_000; index++)
        {
            if (!ReferenceEquals(cached, check.CheckHealthAsync(context)))
                throw new Exception("local health polling must reuse the cached completed Task");
        }
    }

    private sealed class FixedReadiness(SharpLinkHealthStatus status) : ISharpLinkServerReadiness
    {
        public SharpLinkHealthStatus Status { get; } = status;
    }
}
