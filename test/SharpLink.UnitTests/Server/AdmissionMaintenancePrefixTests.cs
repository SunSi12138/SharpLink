using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public sealed class AdmissionMaintenancePrefixTests
{
    [Test]
    [Arguments("concurrency")]
    [Arguments("contract")]
    [Arguments("method")]
    [Arguments("fixed")]
    [Arguments("sliding")]
    [Arguments("token")]
    [Arguments("partition")]
    [Arguments("partition-capacity")]
    public async Task NonWaitingAdmissionMustCompleteInlineBeforeReaderFence(string policy)
    {
        foreach (var kind in new[] { RpcMethodKind.Unary, RpcMethodKind.OneWay })
        {
            var key = "first";
            var options = new SharpLinkAdmissionControlOptions { QueueOneWayCalls = true };
            switch (policy)
            {
                case "concurrency": options.Global.UseConcurrency(1); break;
                case "contract": options.AddContract(1, rule => rule.UseConcurrency(1)); break;
                case "method": options.AddMethod(1, 2, rule => rule.UseConcurrency(1)); break;
                case "fixed":
                    options.Global.UseFixedWindow(rule => { rule.PermitLimit = 1; rule.Window = TimeSpan.FromMinutes(1); });
                    break;
                case "sliding":
                    options.Global.UseSlidingWindow(rule => { rule.PermitLimit = 1; rule.Window = TimeSpan.FromMinutes(1); rule.SegmentsPerWindow = 2; });
                    break;
                case "token":
                    options.Global.UseTokenBucket(rule => { rule.TokenLimit = 1; rule.TokensPerPeriod = 1; rule.ReplenishmentPeriod = TimeSpan.FromMinutes(1); });
                    break;
                case "partition":
                case "partition-capacity":
                    options.UsePartition(_ => key, rule => { rule.MaxPartitions = 1; rule.UseConcurrency(1); });
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(policy));
            }
            await using var controller = SharpLinkAdmissionController.Create(options, [], new ManualTimeProvider());
            Ensure(!controller.WaitingEnabled, "all queue bounds remain disabled, not an empty enabled queue");
            var context = new SharpLinkAdmissionContext(1, 2, kind, "connection", null, null);
            var acquisition = controller.AcquireAsync(context, 32, allowQueue: true, CancellationToken.None);
            Ensure(acquisition.IsCompletedSuccessfully, "non-waiting acquisition cannot suspend its FIFO reader prefix");
            var first = acquisition.Result;
            Ensure(first.IsAcquired, "first immediate permit acquired");
            try
            {
                if (policy == "partition-capacity") key = "second";
                var rejection = controller.AcquireAsync(context, 32, allowQueue: true, CancellationToken.None);
                Ensure(rejection.IsCompletedSuccessfully && !rejection.Result.IsAcquired,
                    "non-waiting rejection cannot create an asynchronous admission continuation");
                Ensure(controller.QueuedCalls == 0 && controller.QueuedBytes == 0,
                    "neither RPC nor OneWay creates queued ownership with zero queue bounds");
            }
            finally { first.Lease!.Dispose(); }
            Ensure(controller.ActivePermits == 0, "existing permit ownership was released");
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception($"assert failed: {message}");
    }
}
