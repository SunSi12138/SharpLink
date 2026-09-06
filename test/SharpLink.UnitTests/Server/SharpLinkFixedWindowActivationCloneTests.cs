using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class SharpLinkFixedWindowActivationCloneTests
{
    [Test]
    public void RuleCloneShouldPreserveExplicitActivation()
    {
        var source = new SharpLinkAdmissionRuleOptions();
        source.UseFixedWindow(rate =>
        {
            rate.PermitLimit = 3;
            rate.Window = TimeSpan.FromSeconds(10);
            rate.UpdateActivation = SharpLinkFixedWindowUpdateActivation.NextWindow;
        });

        var clone = source.CloneRuleValidated();
        var fixedWindow = clone.RateLimit as SharpLinkFixedWindowLimitOptions;
        Ensure(fixedWindow is not null &&
               fixedWindow.UpdateActivation == SharpLinkFixedWindowUpdateActivation.NextWindow,
            "rule clone must preserve explicit FixedWindow activation");
    }

    [Test]
    public void PartitionCloneShouldPreserveExplicitActivation()
    {
        var source = new SharpLinkPartitionAdmissionOptions
        {
            MaxPartitions = 4,
            IdleTimeout = TimeSpan.FromMinutes(1)
        };
        source.UseFixedWindow(rate =>
        {
            rate.PermitLimit = 5;
            rate.Window = TimeSpan.FromSeconds(20);
            rate.UpdateActivation = SharpLinkFixedWindowUpdateActivation.Immediate;
        });

        var clone = source.CloneValidated();
        var fixedWindow = clone.RateLimit as SharpLinkFixedWindowLimitOptions;
        Ensure(fixedWindow is not null &&
               fixedWindow.UpdateActivation == SharpLinkFixedWindowUpdateActivation.Immediate,
            "partition clone must preserve explicit FixedWindow activation");
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new Exception($"assert failed: {scenario}");
    }
}
