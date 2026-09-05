using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class DynamicFixedWindowIntegratedRpcBenchmarks
{
    private BenchmarkEnvironment _currentFresh = null!;
    private BenchmarkEnvironment _dynamicFresh = null!;
    private BenchmarkEnvironment _currentAfterUpdates = null!;
    private BenchmarkEnvironment _dynamicAfterImmediateUpdates = null!;
    private BenchmarkEnvironment _dynamicAfterPendingUpdates = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _currentFresh = await BenchmarkEnvironment.CreateAsync(
            configureBuiltServer: server => server.EnableAdmissionControl(options =>
                ConfigureCurrent(options, 1_000_000_000, TimeSpan.FromHours(1))));

        _dynamicFresh = await BenchmarkEnvironment.CreateAsync(
            configureBuiltServer: server => server.EnableAdmissionControl(options =>
                ConfigureDynamic(
                    options,
                    1_000_000_000,
                    TimeSpan.FromHours(1),
                    DynamicFixedWindowActivationMode.Immediate)));

        _currentAfterUpdates = await BenchmarkEnvironment.CreateAsync(
            configureBuiltServer: server =>
            {
                server.EnableAdmissionControl(options =>
                    ConfigureCurrent(options, 1_000_000_000, TimeSpan.FromHours(1)));
                for (var index = 0; index < 64; index++)
                {
                    var expanded = (index & 1) == 0;
                    server.UpdateAdmissionControl(options => ConfigureCurrent(
                        options,
                        expanded ? 999_000_000 : 1_000_000_000,
                        expanded ? TimeSpan.FromMinutes(55) : TimeSpan.FromHours(1)));
                }
            });

        _dynamicAfterImmediateUpdates = await BenchmarkEnvironment.CreateAsync(
            configureBuiltServer: server =>
            {
                server.EnableAdmissionControl(options => ConfigureDynamic(
                    options,
                    1_000_000_000,
                    TimeSpan.FromHours(1),
                    DynamicFixedWindowActivationMode.Immediate));
                for (var index = 0; index < 64; index++)
                {
                    var expanded = (index & 1) == 0;
                    server.UpdateAdmissionControl(options => ConfigureDynamic(
                        options,
                        expanded ? 999_000_000 : 1_000_000_000,
                        expanded ? TimeSpan.FromMinutes(55) : TimeSpan.FromHours(1),
                        DynamicFixedWindowActivationMode.Immediate));
                }
            });

        _dynamicAfterPendingUpdates = await BenchmarkEnvironment.CreateAsync(
            configureBuiltServer: server =>
            {
                server.EnableAdmissionControl(options => ConfigureDynamic(
                    options,
                    1_000_000_000,
                    TimeSpan.FromHours(1),
                    DynamicFixedWindowActivationMode.NextWindowBoundary));
                for (var index = 0; index < 64; index++)
                {
                    var expanded = (index & 1) == 0;
                    server.UpdateAdmissionControl(options => ConfigureDynamic(
                        options,
                        expanded ? 999_000_000 : 1_000_000_000,
                        expanded ? TimeSpan.FromMinutes(55) : TimeSpan.FromHours(1),
                        DynamicFixedWindowActivationMode.NextWindowBoundary));
                }
            });
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _currentFresh.DisposeAsync();
        await _dynamicFresh.DisposeAsync();
        await _currentAfterUpdates.DisposeAsync();
        await _dynamicAfterImmediateUpdates.DisposeAsync();
        await _dynamicAfterPendingUpdates.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public ValueTask<int> CurrentFixedWindowFresh()
        => _currentFresh.Rpc.AddAsync(10, 20);

    [Benchmark]
    public ValueTask<int> DynamicFixedWindowFresh()
        => _dynamicFresh.Rpc.AddAsync(10, 20);

    [Benchmark]
    public ValueTask<int> CurrentFixedWindowAfter64Updates()
        => _currentAfterUpdates.Rpc.AddAsync(10, 20);

    [Benchmark]
    public ValueTask<int> DynamicImmediateAfter64Updates()
        => _dynamicAfterImmediateUpdates.Rpc.AddAsync(10, 20);

    [Benchmark]
    public ValueTask<int> DynamicNextBoundaryAfter64Updates()
        => _dynamicAfterPendingUpdates.Rpc.AddAsync(10, 20);

    internal static void ConfigureCurrent(
        SharpLinkAdmissionControlOptions options,
        int permitLimit,
        TimeSpan window)
        => options.Global.UseFixedWindow(rate =>
        {
            rate.PermitLimit = permitLimit;
            rate.Window = window;
        });

    internal static void ConfigureDynamic(
        SharpLinkAdmissionControlOptions options,
        int permitLimit,
        TimeSpan window,
        DynamicFixedWindowActivationMode activationMode)
    {
        ConfigureCurrent(options, permitLimit, window);
        options.GlobalFixedWindowActivationModeForPrototype = activationMode;
    }
}

[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class DynamicFixedWindowIntegratedUpdateBenchmarks
{
    private BenchmarkEnvironment _currentEnvironment = null!;
    private BenchmarkEnvironment _immediateEnvironment = null!;
    private BenchmarkEnvironment _nextBoundaryEnvironment = null!;
    private ISharpLinkServer _currentServer = null!;
    private ISharpLinkServer _immediateServer = null!;
    private ISharpLinkServer _nextBoundaryServer = null!;
    private bool _currentToggle;
    private bool _immediateToggle;
    private bool _nextBoundaryToggle;

    [GlobalSetup]
    public async Task Setup()
    {
        _currentEnvironment = await BenchmarkEnvironment.CreateAsync(
            configureBuiltServer: server =>
            {
                _currentServer = server;
                server.EnableAdmissionControl(options =>
                    DynamicFixedWindowIntegratedRpcBenchmarks.ConfigureCurrent(
                        options, 1_000_000_000, TimeSpan.FromHours(1)));
            });
        _immediateEnvironment = await BenchmarkEnvironment.CreateAsync(
            configureBuiltServer: server =>
            {
                _immediateServer = server;
                server.EnableAdmissionControl(options =>
                    DynamicFixedWindowIntegratedRpcBenchmarks.ConfigureDynamic(
                        options,
                        1_000_000_000,
                        TimeSpan.FromHours(1),
                        DynamicFixedWindowActivationMode.Immediate));
            });
        _nextBoundaryEnvironment = await BenchmarkEnvironment.CreateAsync(
            configureBuiltServer: server =>
            {
                _nextBoundaryServer = server;
                server.EnableAdmissionControl(options =>
                    DynamicFixedWindowIntegratedRpcBenchmarks.ConfigureDynamic(
                        options,
                        1_000_000_000,
                        TimeSpan.FromHours(1),
                        DynamicFixedWindowActivationMode.NextWindowBoundary));
            });
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _currentEnvironment.DisposeAsync();
        await _immediateEnvironment.DisposeAsync();
        await _nextBoundaryEnvironment.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public void CurrentFixedWindowUpdate()
    {
        _currentToggle = !_currentToggle;
        _currentServer.UpdateAdmissionControl(options =>
            DynamicFixedWindowIntegratedRpcBenchmarks.ConfigureCurrent(
                options,
                _currentToggle ? 999_000_000 : 1_000_000_000,
                _currentToggle ? TimeSpan.FromMinutes(55) : TimeSpan.FromHours(1)));
    }

    [Benchmark]
    public void DynamicImmediateUpdate()
    {
        _immediateToggle = !_immediateToggle;
        _immediateServer.UpdateAdmissionControl(options =>
            DynamicFixedWindowIntegratedRpcBenchmarks.ConfigureDynamic(
                options,
                _immediateToggle ? 999_000_000 : 1_000_000_000,
                _immediateToggle ? TimeSpan.FromMinutes(55) : TimeSpan.FromHours(1),
                DynamicFixedWindowActivationMode.Immediate));
    }

    [Benchmark]
    public void DynamicNextBoundaryUpdate()
    {
        _nextBoundaryToggle = !_nextBoundaryToggle;
        _nextBoundaryServer.UpdateAdmissionControl(options =>
            DynamicFixedWindowIntegratedRpcBenchmarks.ConfigureDynamic(
                options,
                _nextBoundaryToggle ? 999_000_000 : 1_000_000_000,
                _nextBoundaryToggle ? TimeSpan.FromMinutes(55) : TimeSpan.FromHours(1),
                DynamicFixedWindowActivationMode.NextWindowBoundary));
    }
}
