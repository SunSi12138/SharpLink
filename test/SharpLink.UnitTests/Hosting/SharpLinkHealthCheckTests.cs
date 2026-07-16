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

    private sealed class FixedReadiness(SharpLinkHealthStatus status) : ISharpLinkServerReadiness
    {
        public SharpLinkHealthStatus Status { get; } = status;
    }
}
