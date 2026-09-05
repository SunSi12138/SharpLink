using System.Reflection;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public class ServerLifecycleCoordinatorTests
{
    [Test]
    public void ShutdownStateMachineOwnershipShouldLiveInFocusedCoordinator()
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var serverType = typeof(SharpLinkServer);
        var coordinatorType = typeof(SharpLinkServer.ServerLifecycleCoordinator);
        var lifecycleField = serverType.GetField("_lifecycle", fields)
            ?? throw new Exception("SharpLinkServer must compose a lifecycle coordinator");

        Ensure(lifecycleField.FieldType == coordinatorType,
            "the server lifecycle field must use the focused coordinator type");

        string[] coordinatorOwnedFields =
        [
            "_acceptCts",
            "_forceStopCts",
            "_stateGate",
            "_callsDrained",
            "_runTask",
            "_stopTask",
            "_deferredServiceCleanupTask",
            "_shutdownCleanupObserver",
            "_serviceCleanupObserver",
            "_lastStopDiagnostics",
            "_callDrainSignalState",
            "_lastCallDrainSignalGlobalCalls",
            "_lastCallDrainSignalPendingAdmissions",
            "_lastCallDrainSignalLocalCalls"
        ];

        foreach (var fieldName in coordinatorOwnedFields)
        {
            Ensure(serverType.GetField(fieldName, fields) is null,
                $"SharpLinkServer must not retain lifecycle state-machine field {fieldName}");
            Ensure(coordinatorType.GetField(fieldName, fields) is not null,
                $"ServerLifecycleCoordinator must own lifecycle field {fieldName}");
        }
    }

    [Test]
    public void CoordinatorShouldDeclareTheDrainAndCleanupOperations()
    {
        const BindingFlags methods = BindingFlags.Instance | BindingFlags.NonPublic;
        var coordinatorType = typeof(SharpLinkServer.ServerLifecycleCoordinator);

        string[] operationNames =
        [
            "StopCoreAsync",
            "CleanupAfterRunFailureAsync",
            "SendGoAwayToAllAsync",
            "FlushAllSessionsAsync",
            "DisposeAllSessionsAsync",
            "DisposeRegisteredServicesAsync"
        ];

        foreach (var operationName in operationNames)
        {
            Ensure(coordinatorType.GetMethod(operationName, methods) is not null,
                $"lifecycle coordinator must own {operationName}");
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
