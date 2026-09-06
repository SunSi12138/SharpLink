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
public class Issue410FinalFixedBenchmarks
{
    private SharpLinkServer _freshPermitServer = null!;
    private SharpLinkServer _freshRejectServer = null!;
    private SharpLinkServer _afterLimitUpdatesServer = null!;
    private SharpLinkServer _afterWindowUpdatesServer = null!;
    private SharpLinkServer _limitUpdateServer = null!;
    private SharpLinkServer _windowUpdateServer = null!;
    private SharpLinkAdmissionContext _context = null!;
    private int _limitToggle;
    private int _windowToggle;

    [GlobalSetup]
    public async Task Setup()
    {
        _context = new SharpLinkAdmissionContext(
            1, 2, RpcMethodKind.Unary, "issue-410-final", null, null);

        _freshPermitServer = CreateServer(1_000_000_000, TimeSpan.FromHours(1));
        _freshRejectServer = CreateServer(1, TimeSpan.FromHours(1));
        await ConsumeAsync(CurrentController(_freshRejectServer));

        _afterLimitUpdatesServer = CreateServer(1_000_000_000, TimeSpan.FromHours(1));
        for (var index = 0; index < 64; index++)
        {
            var limit = (index & 1) == 0 ? 999_000_000 : 1_000_000_000;
            Update(_afterLimitUpdatesServer, limit, TimeSpan.FromHours(1));
        }

        _afterWindowUpdatesServer = CreateServer(1_000_000_000, TimeSpan.FromHours(1));
        for (var index = 0; index < 64; index++)
        {
            var window = (index & 1) == 0 ? TimeSpan.FromMinutes(59) : TimeSpan.FromHours(1);
            Update(_afterWindowUpdatesServer, 1_000_000_000, window);
        }

        _limitUpdateServer = CreateServer(1_000_000_000, TimeSpan.FromHours(1));
        _windowUpdateServer = CreateServer(1_000_000_000, TimeSpan.FromHours(1));
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _freshPermitServer.DisposeAsync();
        await _freshRejectServer.DisposeAsync();
        await _afterLimitUpdatesServer.DisposeAsync();
        await _afterWindowUpdatesServer.DisposeAsync();
        await _limitUpdateServer.DisposeAsync();
        await _windowUpdateServer.DisposeAsync();
    }

    [Benchmark]
    public void FreshPermit()
        => AcquireAndDispose(CurrentController(_freshPermitServer));

    [Benchmark]
    public bool FreshReject()
        => CurrentController(_freshRejectServer)
            .AcquireAsync(_context, 1, false, CancellationToken.None).Result.IsAcquired;

    [Benchmark]
    public void PermitAfter64LimitUpdates()
        => AcquireAndDispose(CurrentController(_afterLimitUpdatesServer));

    [Benchmark]
    public void PermitAfter64WindowUpdates()
        => AcquireAndDispose(CurrentController(_afterWindowUpdatesServer));

    [Benchmark]
    public void LimitUpdate()
    {
        var limit = (Interlocked.Increment(ref _limitToggle) & 1) == 0
            ? 999_000_000
            : 1_000_000_000;
        Update(_limitUpdateServer, limit, TimeSpan.FromHours(1));
    }

    [Benchmark]
    public void WindowUpdate()
    {
        var window = (Interlocked.Increment(ref _windowToggle) & 1) == 0
            ? TimeSpan.FromMinutes(59)
            : TimeSpan.FromHours(1);
        Update(_windowUpdateServer, 1_000_000_000, window);
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

    private static SharpLinkServer CreateServer(int permitLimit, TimeSpan window)
    {
        var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .Build();
        ((ISharpLinkServer)server).EnableAdmissionControl(options =>
            Configure(options, permitLimit, window));
        return server;
    }

    private static void Update(SharpLinkServer server, int permitLimit, TimeSpan window)
        => ((ISharpLinkServer)server).UpdateAdmissionControl(options =>
            Configure(options, permitLimit, window));

    private static void Configure(
        SharpLinkAdmissionControlOptions options,
        int permitLimit,
        TimeSpan window)
        => options.Global.UseFixedWindow(rate =>
        {
            rate.PermitLimit = permitLimit;
            rate.Window = window;
        });

    private static SharpLinkAdmissionController CurrentController(SharpLinkServer server)
        => server.CurrentAdmissionProgramForTests?.Controller ??
           throw new InvalidOperationException("Admission control is not enabled.");
}
