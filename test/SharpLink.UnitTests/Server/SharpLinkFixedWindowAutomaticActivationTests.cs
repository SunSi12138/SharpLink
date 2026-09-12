using System.Threading.RateLimiting;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class SharpLinkFixedWindowAutomaticActivationTests
{
    [Test]
    public void SameActiveWindowShouldInferImmediateWithoutResettingConsumption()
    {
        using var source = CreateFixed(2, TimeSpan.FromSeconds(10));
        Ensure(Acquire(source), "source must consume one permit before the update");

        using var target = CreateFixed(3, TimeSpan.FromSeconds(10), source);
        source.CommitTransitionTo(target);
        target.OnPublished();

        Ensure(target.FixedWindowForTests!.ActivationModeForTests ==
               SharpLinkFixedWindowUpdateActivation.Immediate,
            "Automatic must resolve a same-active-Window target to Immediate");
        Ensure(target.FixedWindowForTests.ConsumedForTests == 1,
            "Automatic Immediate must keep current-window consumption");
        Ensure(Acquire(target) && Acquire(target) && !Acquire(target),
            "limit 2 -> 3 with one prior grant may expose exactly two additional permits");
    }

    private static AdmissionRateState CreateFixed(
        int permitLimit,
        TimeSpan window,
        AdmissionRateState? source = null)
    {
        var rule = new SharpLinkAdmissionRuleOptions();
        rule.UseFixedWindow(options =>
        {
            options.PermitLimit = permitLimit;
            options.Window = window;
            options.UpdateActivation = SharpLinkFixedWindowUpdateActivation.Automatic;
        });
        return AdmissionRateState.Create(rule, TimeProvider.System, source);
    }

    private static bool Acquire(RateLimiter limiter)
    {
        using var lease = limiter.AttemptAcquire(1);
        return lease.IsAcquired;
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }
}
