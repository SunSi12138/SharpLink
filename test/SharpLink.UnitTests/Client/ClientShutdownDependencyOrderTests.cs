using System.Linq;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public class ClientShutdownDependencyOrderTests
{
    [Test]
    public void ShutdownShouldReleaseDependantsBeforeDependencies()
    {
        var order = SharpLinkClient.GetShutdownDependencyOrder(
            ["ModuleB", "ModuleA", "ModuleC"],
            [[], ["ModuleB"], ["ModuleA"]]);

        Ensure(order.SequenceEqual([2, 1, 0]),
            "shutdown must release ModuleC before ModuleA before ModuleB so the normal unregister dependant guard remains valid");
    }

    [Test]
    public void ShutdownOrderShouldRemainDeterministicForIndependentModules()
    {
        var order = SharpLinkClient.GetShutdownDependencyOrder(
            ["ModuleA", "ModuleB", "ModuleC"],
            [[], [], []]);

        Ensure(order.SequenceEqual([0, 1, 2]),
            "independent dynamic modules should retain deterministic registration-snapshot order during shutdown");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
