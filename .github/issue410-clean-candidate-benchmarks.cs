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
public class Issue410CleanFixedWindowControllerBenchmarks
{
    private readonly SharpLinkAdmissionContext _context = new(
        101,
        202,
        RpcMethodKind.Unary,
        "issue410-clean-controller",
        null,
        null);

    private SharpLinkServer _freshPermitServer = null!;
    private SharpLinkServer _freshRejectServer = null!;
    private SharpLinkServer _afterUpdatesServer = null!;
    private SharpLinkServer _multiScopeServer = null!;
    private SharpLinkServer _multiScopeAfterUpdatesServer = null!;
    private SharpLinkAdmissionController _freshPermit = null!;
    private SharpLinkAdmissionController _freshReject = null!;
    private SharpLinkAdmissionController _afterUpdates = null!;
    private SharpLinkAdmissionController _multiScope = null!;
    private SharpLinkAdmissionController _multiScopeAfterUpdates = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _freshPermitServer = CreateServer();
        var freshPermit = (ISharpLinkServer)_freshPermitServer;
        freshPermit.EnableAdmissionControl(options => ConfigureGlobal(options, 1_000_000_000, TimeSpan.FromHours(1)));
        _freshPermit = CurrentController(_freshPermitServer);

        _freshRejectServer = CreateServer();
        var freshReject = (ISharpLinkServer)_freshRejectServer;
        freshReject.EnableAdmissionControl(options => ConfigureGlobal(options, 1, TimeSpan.FromHours(1)));
        _freshReject = CurrentController(_freshRejectServer);
        await ConsumeOneAsync(_freshReject);

        _afterUpdatesServer = CreateServer();
        var afterUpdates = (ISharpLinkServer)_afterUpdatesServer;
        afterUpdates.EnableAdmissionControl(options => ConfigureGlobal(options, 1_000_000_000, TimeSpan.FromHours(1)));
        for (var index = 0; index < 64; index++)
        {
            var limit = (index & 1) == 0 ? 999_000_000 : 1_000_000_000;
            afterUpdates.UpdateAdmissionControl(options => ConfigureGlobal(options, limit, TimeSpan.FromHours(1)));
        }
        _afterUpdates = CurrentController(_afterUpdatesServer);

        _multiScopeServer = CreateServer();
        var multiScope = (ISharpLinkServer)_multiScopeServer;
        multiScope.EnableAdmissionControl(options => ConfigureScopes(options, 1_000_000_000));
        _multiScope = CurrentController(_multiScopeServer);

        _multiScopeAfterUpdatesServer = CreateServer();
        var multiScopeAfterUpdates = (ISharpLinkServer)_multiScopeAfterUpdatesServer;
        multiScopeAfterUpdates.EnableAdmissionControl(options => ConfigureScopes(options, 1_000_000_000));
        for (var index = 0; index < 64; index++)
        {
            var limit = (index & 1) == 0 ? 999_000_000 : 1_000_000_000;
            multiScopeAfterUpdates.UpdateAdmissionControl(options => ConfigureScopes(options, limit));
        }
        _multiScopeAfterUpdates = CurrentController(_multiScopeAfterUpdatesServer);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _freshPermitServer.DisposeAsync();
        await _freshRejectServer.DisposeAsync();
        await _afterUpdatesServer.DisposeAsync();
        await _multiScopeServer.DisposeAsync();
        await _multiScopeAfterUpdatesServer.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public bool FreshPermit() => AcquireAndDispose(_freshPermit);

    [Benchmark]
    public bool FreshReject()
        => _freshReject.AcquireAsync(_context, 1, false, CancellationToken.None).Result.IsAcquired;

    [Benchmark]
    public bool After64LimitUpdatesPermit() => AcquireAndDispose(_afterUpdates);

    [Benchmark]
    public bool MultiScopeFreshPermit() => AcquireAndDispose(_multiScope);

    [Benchmark]
    public bool MultiScopeAfter64LimitUpdatesPermit() => AcquireAndDispose(_multiScopeAfterUpdates);

    private bool AcquireAndDispose(SharpLinkAdmissionController controller)
    {
        var decision = controller.AcquireAsync(
            _context,
            1,
            false,
            CancellationToken.None).Result;
        decision.Lease?.Dispose();
        return decision.IsAcquired;
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
        => (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .Build();

    private static SharpLinkAdmissionController CurrentController(SharpLinkServer server)
        => server.CurrentAdmissionProgramForTests?.Controller ??
           throw new InvalidOperationException("Expected an enabled admission program.");

    private static void ConfigureGlobal(
        SharpLinkAdmissionControlOptions options,
        int limit,
        TimeSpan window)
        => options.Global.UseFixedWindow(rate =>
        {
            rate.PermitLimit = limit;
            rate.Window = window;
        });

    private static void ConfigureScopes(SharpLinkAdmissionControlOptions options, int limit)
    {
        ConfigureGlobal(options, limit, TimeSpan.FromHours(1));
        options.AddContract(101, rule => rule.UseFixedWindow(rate =>
        {
            rate.PermitLimit = limit;
            rate.Window = TimeSpan.FromHours(1);
        }));
        options.AddMethod(101, 202, rule => rule.UseFixedWindow(rate =>
        {
            rate.PermitLimit = limit;
            rate.Window = TimeSpan.FromHours(1);
        }));
    }
}

[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class Issue410CleanFixedWindowDirectBenchmarks
{
    private AdmissionRateState _permit = null!;
    private AdmissionRateState _reject = null!;

    [GlobalSetup]
    public void Setup()
    {
        _permit = CreateState(1_000_000_000, TimeSpan.FromHours(1));
        _reject = CreateState(1, TimeSpan.FromHours(1));
        using var consumed = _reject.AttemptAcquire(1);
        if (!consumed.IsAcquired)
            throw new InvalidOperationException("Failed to exhaust reject benchmark state.");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _permit.Dispose();
        _reject.Dispose();
    }

    [Benchmark(Baseline = true)]
    public bool Permit()
    {
        using var lease = _permit.AttemptAcquire(1);
        return lease.IsAcquired;
    }

    [Benchmark]
    public bool Reject()
    {
        using var lease = _reject.AttemptAcquire(1);
        return lease.IsAcquired;
    }

    private static AdmissionRateState CreateState(int limit, TimeSpan window)
    {
        var rule = new SharpLinkAdmissionRuleOptions();
        rule.UseFixedWindow(rate =>
        {
            rate.PermitLimit = limit;
            rate.Window = window;
        });
        return AdmissionRateState.Create(rule, TimeProvider.System);
    }
}

[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class Issue410CleanFixedWindowUpdateBenchmarks
{
    private SharpLinkServer _limitServer = null!;
    private SharpLinkServer _windowServer = null!;
    private SharpLinkServer _multiScopeServer = null!;
    private ISharpLinkServer _limit = null!;
    private ISharpLinkServer _window = null!;
    private ISharpLinkServer _multiScope = null!;
    private int _limitToggle;
    private int _windowToggle;
    private int _multiScopeToggle;

    [GlobalSetup]
    public void Setup()
    {
        _limitServer = CreateServer();
        _limit = (ISharpLinkServer)_limitServer;
        _limit.EnableAdmissionControl(options => ConfigureGlobal(options, 1_000_000_000, TimeSpan.FromHours(1)));

        _windowServer = CreateServer();
        _window = (ISharpLinkServer)_windowServer;
        _window.EnableAdmissionControl(options => ConfigureGlobal(options, 1_000_000_000, TimeSpan.FromHours(1)));

        _multiScopeServer = CreateServer();
        _multiScope = (ISharpLinkServer)_multiScopeServer;
        _multiScope.EnableAdmissionControl(options => ConfigureScopes(options, 1_000_000_000));
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _limitServer.DisposeAsync();
        await _windowServer.DisposeAsync();
        await _multiScopeServer.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public void LimitOnlyUpdate()
    {
        var limit = (Interlocked.Increment(ref _limitToggle) & 1) == 0
            ? 999_000_000
            : 1_000_000_000;
        _limit.UpdateAdmissionControl(options => ConfigureGlobal(options, limit, TimeSpan.FromHours(1)));
    }

    [Benchmark]
    public void WindowChangeUpdate()
    {
        var window = (Interlocked.Increment(ref _windowToggle) & 1) == 0
            ? TimeSpan.FromMinutes(55)
            : TimeSpan.FromHours(1);
        _window.UpdateAdmissionControl(options => ConfigureGlobal(options, 1_000_000_000, window));
    }

    [Benchmark]
    public void MultiScopeLimitOnlyUpdate()
    {
        var limit = (Interlocked.Increment(ref _multiScopeToggle) & 1) == 0
            ? 999_000_000
            : 1_000_000_000;
        _multiScope.UpdateAdmissionControl(options => ConfigureScopes(options, limit));
    }

    private static SharpLinkServer CreateServer()
        => (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .Build();

    private static void ConfigureGlobal(
        SharpLinkAdmissionControlOptions options,
        int limit,
        TimeSpan window)
        => options.Global.UseFixedWindow(rate =>
        {
            rate.PermitLimit = limit;
            rate.Window = window;
        });

    private static void ConfigureScopes(SharpLinkAdmissionControlOptions options, int limit)
    {
        ConfigureGlobal(options, limit, TimeSpan.FromHours(1));
        options.AddContract(101, rule => rule.UseFixedWindow(rate =>
        {
            rate.PermitLimit = limit;
            rate.Window = TimeSpan.FromHours(1);
        }));
        options.AddMethod(101, 202, rule => rule.UseFixedWindow(rate =>
        {
            rate.PermitLimit = limit;
            rate.Window = TimeSpan.FromHours(1);
        }));
    }
}
