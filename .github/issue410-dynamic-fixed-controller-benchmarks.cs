using System.Net;
using System.Threading;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using SharpLink.Abstractions;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class DynamicFixedWindowIntegratedControllerBenchmarks
{
    private readonly SharpLinkAdmissionContext _context = new(
        101,
        202,
        RpcMethodKind.Unary,
        "dynamic-fixed-controller-benchmark",
        null,
        null);

    private SharpLinkServer _currentPermitServer = null!;
    private SharpLinkServer _dynamicPermitServer = null!;
    private SharpLinkServer _currentRejectServer = null!;
    private SharpLinkServer _dynamicRejectServer = null!;
    private SharpLinkServer _currentAfterUpdatesServer = null!;
    private SharpLinkServer _dynamicImmediateAfterUpdatesServer = null!;
    private SharpLinkServer _dynamicPendingAfterUpdatesServer = null!;

    private SharpLinkAdmissionController _currentPermit = null!;
    private SharpLinkAdmissionController _dynamicPermit = null!;
    private SharpLinkAdmissionController _currentReject = null!;
    private SharpLinkAdmissionController _dynamicReject = null!;
    private SharpLinkAdmissionController _currentAfterUpdates = null!;
    private SharpLinkAdmissionController _dynamicImmediateAfterUpdates = null!;
    private SharpLinkAdmissionController _dynamicPendingAfterUpdates = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _currentPermitServer = CreateServer();
        ((ISharpLinkServer)_currentPermitServer).EnableAdmissionControl(options =>
            ConfigureCurrent(options, 1_000_000_000, TimeSpan.FromHours(1)));
        _currentPermit = CurrentController(_currentPermitServer);

        _dynamicPermitServer = CreateServer();
        ((ISharpLinkServer)_dynamicPermitServer).EnableAdmissionControl(options =>
            ConfigureDynamic(
                options,
                1_000_000_000,
                TimeSpan.FromHours(1),
                DynamicFixedWindowActivationMode.Immediate));
        _dynamicPermit = CurrentController(_dynamicPermitServer);

        _currentRejectServer = CreateServer();
        ((ISharpLinkServer)_currentRejectServer).EnableAdmissionControl(options =>
            ConfigureCurrent(options, 1, TimeSpan.FromHours(1)));
        _currentReject = CurrentController(_currentRejectServer);
        await ConsumeOneAsync(_currentReject);

        _dynamicRejectServer = CreateServer();
        ((ISharpLinkServer)_dynamicRejectServer).EnableAdmissionControl(options =>
            ConfigureDynamic(
                options,
                1,
                TimeSpan.FromHours(1),
                DynamicFixedWindowActivationMode.Immediate));
        _dynamicReject = CurrentController(_dynamicRejectServer);
        await ConsumeOneAsync(_dynamicReject);

        _currentAfterUpdatesServer = CreateServer();
        var currentAfterUpdates = (ISharpLinkServer)_currentAfterUpdatesServer;
        currentAfterUpdates.EnableAdmissionControl(options =>
            ConfigureCurrent(options, 1_000_000_000, TimeSpan.FromHours(1)));
        ApplyCurrentUpdates(currentAfterUpdates);
        _currentAfterUpdates = CurrentController(_currentAfterUpdatesServer);

        _dynamicImmediateAfterUpdatesServer = CreateServer();
        var dynamicImmediate = (ISharpLinkServer)_dynamicImmediateAfterUpdatesServer;
        dynamicImmediate.EnableAdmissionControl(options => ConfigureDynamic(
            options,
            1_000_000_000,
            TimeSpan.FromHours(1),
            DynamicFixedWindowActivationMode.Immediate));
        ApplyDynamicUpdates(dynamicImmediate, DynamicFixedWindowActivationMode.Immediate);
        _dynamicImmediateAfterUpdates = CurrentController(_dynamicImmediateAfterUpdatesServer);

        _dynamicPendingAfterUpdatesServer = CreateServer();
        var dynamicPending = (ISharpLinkServer)_dynamicPendingAfterUpdatesServer;
        dynamicPending.EnableAdmissionControl(options => ConfigureDynamic(
            options,
            1_000_000_000,
            TimeSpan.FromHours(1),
            DynamicFixedWindowActivationMode.NextWindowBoundary));
        ApplyDynamicUpdates(dynamicPending, DynamicFixedWindowActivationMode.NextWindowBoundary);
        _dynamicPendingAfterUpdates = CurrentController(_dynamicPendingAfterUpdatesServer);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _currentPermitServer.DisposeAsync();
        await _dynamicPermitServer.DisposeAsync();
        await _currentRejectServer.DisposeAsync();
        await _dynamicRejectServer.DisposeAsync();
        await _currentAfterUpdatesServer.DisposeAsync();
        await _dynamicImmediateAfterUpdatesServer.DisposeAsync();
        await _dynamicPendingAfterUpdatesServer.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public void CurrentFreshPermit()
        => AcquireAndDispose(_currentPermit);

    [Benchmark]
    public void DynamicFreshPermit()
        => AcquireAndDispose(_dynamicPermit);

    [Benchmark]
    public bool CurrentFreshReject()
        => _currentReject.AcquireAsync(
            _context,
            1,
            false,
            CancellationToken.None).Result.IsAcquired;

    [Benchmark]
    public bool DynamicFreshReject()
        => _dynamicReject.AcquireAsync(
            _context,
            1,
            false,
            CancellationToken.None).Result.IsAcquired;

    [Benchmark]
    public void CurrentAfter64UpdatesPermit()
        => AcquireAndDispose(_currentAfterUpdates);

    [Benchmark]
    public void DynamicImmediateAfter64UpdatesPermit()
        => AcquireAndDispose(_dynamicImmediateAfterUpdates);

    [Benchmark]
    public void DynamicNextBoundaryAfter64UpdatesPermit()
        => AcquireAndDispose(_dynamicPendingAfterUpdates);

    private void AcquireAndDispose(SharpLinkAdmissionController controller)
    {
        var decision = controller.AcquireAsync(
            _context,
            1,
            false,
            CancellationToken.None).Result;
        decision.Lease!.Dispose();
    }

    private async Task ConsumeOneAsync(SharpLinkAdmissionController controller)
    {
        var decision = await controller.AcquireAsync(
            _context,
            1,
            false,
            CancellationToken.None);
        decision.Lease!.Dispose();
    }

    private static SharpLinkServer CreateServer()
    {
        var builder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback.ToString());
        return (SharpLinkServer)builder.Build();
    }

    private static SharpLinkAdmissionController CurrentController(SharpLinkServer server)
        => server.CurrentAdmissionProgramForTests?.Controller ??
           throw new InvalidOperationException("Expected an enabled admission program.");

    private static void ApplyCurrentUpdates(ISharpLinkServer server)
    {
        for (var index = 0; index < 64; index++)
        {
            var alternate = (index & 1) == 0;
            server.UpdateAdmissionControl(options => ConfigureCurrent(
                options,
                alternate ? 999_000_000 : 1_000_000_000,
                alternate ? TimeSpan.FromMinutes(55) : TimeSpan.FromHours(1)));
        }
    }

    private static void ApplyDynamicUpdates(
        ISharpLinkServer server,
        DynamicFixedWindowActivationMode activationMode)
    {
        for (var index = 0; index < 64; index++)
        {
            var alternate = (index & 1) == 0;
            server.UpdateAdmissionControl(options => ConfigureDynamic(
                options,
                alternate ? 999_000_000 : 1_000_000_000,
                alternate ? TimeSpan.FromMinutes(55) : TimeSpan.FromHours(1),
                activationMode));
        }
    }

    private static void ConfigureCurrent(
        SharpLinkAdmissionControlOptions options,
        int permitLimit,
        TimeSpan window)
        => options.Global.UseFixedWindow(rate =>
        {
            rate.PermitLimit = permitLimit;
            rate.Window = window;
        });

    private static void ConfigureDynamic(
        SharpLinkAdmissionControlOptions options,
        int permitLimit,
        TimeSpan window,
        DynamicFixedWindowActivationMode activationMode)
    {
        ConfigureCurrent(options, permitLimit, window);
        options.GlobalFixedWindowActivationModeForPrototype = activationMode;
    }
}
