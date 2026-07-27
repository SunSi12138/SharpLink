using DemoBase;
using SharpLink.Abstractions;
using SharpLink.Sdk;

var port = DemoStream.GetFreePort();
using var app = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var server = DemoTcp.CreateServer<IAdmissionService, AdmissionService>(port, builder =>
    builder.UseAdmissionControl(options => options.Global.UseConcurrency(1)));
var serverTask = DemoTcp.StartServerAsync(server, app.Token);
var client = DemoTcp.CreateClient(port);

try
{
    await DemoTcp.EnsureConnectedAsync(client, app.Token);
    var service = client.Get<IAdmissionService>();
    var admitted = service.HoldAsync(400, app.Token).AsTask();
    await AdmissionService.Started.Task.WaitAsync(app.Token);

    var rejected = 0;
    for (var index = 0; index < 3; index++)
    {
        try
        {
            _ = await service.HoldAsync(10, app.Token);
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ResourceExhausted)
        {
            rejected++;
        }
    }

    Console.WriteLine($"admitted={await admitted}, rejected={rejected}");
    if (rejected != 3)
        throw new InvalidOperationException("The admission limit did not reject every overflow call.");
}
finally
{
    await DemoTcp.ShutdownAsync(app, serverTask, client, server);
}

[RpcContract]
public interface IAdmissionService : IService
{
    ValueTask<int> HoldAsync(int milliseconds, CancellationToken cancellationToken);
}

[RpcService]
public sealed class AdmissionService : IAdmissionService
{
    internal static TaskCompletionSource Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask<int> HoldAsync(int milliseconds, CancellationToken cancellationToken)
    {
        Started.TrySetResult();
        await Task.Delay(milliseconds, cancellationToken);
        return milliseconds;
    }
}
