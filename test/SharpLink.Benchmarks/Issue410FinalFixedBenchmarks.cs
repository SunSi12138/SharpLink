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
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 2, iterationCount: 6)]
public class Issue410FinalFixedBenchmarks
{
    private SharpLinkAdmissionController _freshPermit = null!;
    private SharpLinkAdmissionController _freshReject = null!;
    private SharpLinkAdmissionController _afterLimitUpdates = null!;
    private SharpLinkAdmissionController _afterWindowUpdates = null!;
    private SharpLinkServer _limitSteadyServer = null!;
    private SharpLinkServer _windowSteadyServer = null!;
    private SharpLinkServer _limitUpdateServer = null!;
    private SharpLinkServer _windowUpdateServer = null!;
    private ISharpLinkServer _limitUpdateControl = null!;
    private ISharpLinkServer _windowUpdateControl = null!;
    private SharpLinkAdmissionContext _context = null!;
    private int _limitToggle;
    private int _windowToggle;

    [GlobalSetup]
    public async Task Setup()
    {
        _context = new SharpLinkAdmissionContext(
            1, 2, RpcMethodKind.Unary, "issue-410-final-perf", null, null);
        _freshPermit = CreateController(1_000_000_000, TimeSpan.FromHours(1));
        _freshReject = CreateController(1, TimeSpan.FromHours(1));
        await ConsumeAsync(_freshReject);

        _limitSteadyServer = CreateServer();
        var limitSteadyControl = (ISharpLinkServer)_limitSteadyServer;
        limitSteadyControl.EnableAdmissionControl(options =>
            ConfigureFixed(options, 1_000_000_000, TimeSpan.FromHours(1)));
        for (var index = 0; index < 64; index++)
        {
            var limit = (index & 1) == 0 ? 999_000_000 : 1_000_000_000;
            limitSteadyControl.UpdateAdmissionControl(options =>
                ConfigureFixed(options, limit, TimeSpan.FromHours(1)));
        }
        _afterLimitUpdates = CurrentController(_limitSteadyServer);

        _windowSteadyServer = CreateServer();
        var windowSteadyControl = (ISharpLinkServer)_windowSteadyServer;
        windowSteadyControl.EnableAdmissionControl(options =>
            ConfigureFixed(options, 1_000_000_000, TimeSpan.FromHours(1)));
        for (var index = 0; index < 64; index++)
        {
            var window = (index & 1) == 0 ? TimeSpan.FromHours(2) : TimeSpan.FromHours(1);
            windowSteadyControl.UpdateAdmissionControl(options =>
                ConfigureFixed(options, 1_000_000_000, window));
        }
        _afterWindowUpdates = CurrentController(_windowSteadyServer);

        _limitUpdateServer = CreateServer();
        _limitUpdateControl = (ISharpLinkServer)_limitUpdateServer;
        _limitUpdateControl.EnableAdmissionControl(options =>
            ConfigureFixed(options, 1_000_000_000, TimeSpan.FromHours(1)));

        _windowUpdateServer = CreateServer();
        _windowUpdateControl = (ISharpLinkServer)_windowUpdateServer;
        _windowUpdateControl.EnableAdmissionControl(options =>
            ConfigureFixed(options, 1_000_000_000, TimeSpan.FromHours(1)));
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _freshPermit.DisposeAsync();
        await _freshReject.DisposeAsync();
        await _limitSteadyServer.DisposeAsync();
        await _windowSteadyServer.DisposeAsync();
        await _limitUpdateServer.DisposeAsync();
        await _windowUpdateServer.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public void FreshPermit() => AcquireAndDispose(_freshPermit);

    [Benchmark]
    public bool FreshReject()
        => _freshReject.AcquireAsync(_context, 1, false, CancellationToken.None).Result.IsAcquired;

    [Benchmark]
    public void PermitAfter64LimitUpdates() => AcquireAndDispose(_afterLimitUpdates);

    [Benchmark]
    public void PermitAfter64WindowUpdates() => AcquireAndDispose(_afterWindowUpdates);

    [Benchmark]
    public void LimitUpdateControlPath()
    {
        var limit = (Interlocked.Increment(ref _limitToggle) & 1) == 0
            ? 1_000_000_000
            : 999_000_000;
        _limitUpdateControl.UpdateAdmissionControl(options =>
            ConfigureFixed(options, limit, TimeSpan.FromHours(1)));
    }

    [Benchmark]
    public void WindowUpdateControlPath()
    {
        var window = (Interlocked.Increment(ref _windowToggle) & 1) == 0
            ? TimeSpan.FromHours(1)
            : TimeSpan.FromHours(2);
        _windowUpdateControl.UpdateAdmissionControl(options =>
            ConfigureFixed(options, 1_000_000_000, window));
    }

    private void AcquireAndDispose(SharpLinkAdmissionController controller)
    {
        var decision = controller.AcquireAsync(
            _context, 1, false, CancellationToken.None).Result;
        decision.Lease!.Dispose();
    }

    private async Task ConsumeAsync(SharpLinkAdmissionController controller)
    {
        var decision = await controller.AcquireAsync(
            _context, 1, false, CancellationToken.None);
        decision.Lease!.Dispose();
    }

    private static SharpLinkAdmissionController CreateController(int permitLimit, TimeSpan window)
    {
        var options = new SharpLinkAdmissionControlOptions();
        ConfigureFixed(options, permitLimit, window);
        return SharpLinkAdmissionController.Create(options, []);
    }

    private static SharpLinkServer CreateServer()
        => (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .Build();

    private static SharpLinkAdmissionController CurrentController(SharpLinkServer server)
        => server.CurrentAdmissionProgramForTests?.Controller ??
           throw new InvalidOperationException("Expected an enabled admission program.");

    private static void ConfigureFixed(
        SharpLinkAdmissionControlOptions options,
        int permitLimit,
        TimeSpan window)
        => options.Global.UseFixedWindow(rate =>
        {
            rate.PermitLimit = permitLimit;
            rate.Window = window;
        });
}
