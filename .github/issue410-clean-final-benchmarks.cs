using System.Net;
using System.Reflection;
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
public class Issue410CleanFinalControllerBenchmarks
{
    private readonly SharpLinkAdmissionContext _context = new(
        101, 202, RpcMethodKind.Unary, "issue410-clean-final", null, null);

    private SharpLinkServer _freshServer = null!;
    private SharpLinkServer _rejectServer = null!;
    private SharpLinkServer _immediateServer = null!;
    private SharpLinkServer _nextServer = null!;
    private SharpLinkServer _multiScopeServer = null!;
    private SharpLinkAdmissionController _fresh = null!;
    private SharpLinkAdmissionController _reject = null!;
    private SharpLinkAdmissionController _immediate = null!;
    private SharpLinkAdmissionController _next = null!;
    private SharpLinkAdmissionController _multiScope = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _freshServer = CreateServer();
        var fresh = (ISharpLinkServer)_freshServer;
        fresh.EnableAdmissionControl(options => ConfigureGlobal(options, 1_000_000_000, TimeSpan.FromHours(1)));
        _fresh = Current(_freshServer);

        _rejectServer = CreateServer();
        var reject = (ISharpLinkServer)_rejectServer;
        reject.EnableAdmissionControl(options => ConfigureGlobal(options, 1, TimeSpan.FromHours(1)));
        _reject = Current(_rejectServer);
        using (var consumed = (await _reject.AcquireAsync(_context, 1, false, CancellationToken.None)).Lease)
        {
        }

        _immediateServer = CreateServer();
        var immediate = (ISharpLinkServer)_immediateServer;
        immediate.EnableAdmissionControl(options => ConfigureGlobal(options, 1_000_000_000, TimeSpan.FromHours(1)));
        for (var index = 0; index < 64; index++)
        {
            var limit = (index & 1) == 0 ? 999_000_000 : 1_000_000_000;
            immediate.UpdateAdmissionControl(options =>
                ConfigureGlobal(options, limit, TimeSpan.FromHours(1), Activation.Immediate));
        }
        _immediate = Current(_immediateServer);

        _nextServer = CreateServer();
        var next = (ISharpLinkServer)_nextServer;
        next.EnableAdmissionControl(options => ConfigureGlobal(options, 1_000_000_000, TimeSpan.FromHours(1)));
        for (var index = 0; index < 64; index++)
        {
            var limit = (index & 1) == 0 ? 999_000_000 : 1_000_000_000;
            next.UpdateAdmissionControl(options =>
                ConfigureGlobal(options, limit, TimeSpan.FromHours(1), Activation.NextWindow));
        }
        _next = Current(_nextServer);

        _multiScopeServer = CreateServer();
        var multiScope = (ISharpLinkServer)_multiScopeServer;
        multiScope.EnableAdmissionControl(options => ConfigureScopes(options, 1_000_000_000));
        for (var index = 0; index < 64; index++)
        {
            var limit = (index & 1) == 0 ? 999_000_000 : 1_000_000_000;
            multiScope.UpdateAdmissionControl(options => ConfigureScopes(options, limit, Activation.Immediate));
        }
        _multiScope = Current(_multiScopeServer);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _freshServer.DisposeAsync();
        await _rejectServer.DisposeAsync();
        await _immediateServer.DisposeAsync();
        await _nextServer.DisposeAsync();
        await _multiScopeServer.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public bool FreshPermit() => Acquire(_fresh);

    [Benchmark]
    public bool FreshReject()
        => _reject.AcquireAsync(_context, 1, false, CancellationToken.None).Result.IsAcquired;

    [Benchmark]
    public bool After64ImmediateLimitUpdates() => Acquire(_immediate);

    [Benchmark]
    public bool After64NextWindowLimitUpdates() => Acquire(_next);

    [Benchmark]
    public bool MultiScopeAfter64ImmediateUpdates() => Acquire(_multiScope);

    private bool Acquire(SharpLinkAdmissionController controller)
    {
        var decision = controller.AcquireAsync(_context, 1, false, CancellationToken.None).Result;
        decision.Lease?.Dispose();
        return decision.IsAcquired;
    }

    private static SharpLinkAdmissionController Current(SharpLinkServer server)
        => server.CurrentAdmissionProgramForTests?.Controller ??
           throw new InvalidOperationException("Expected enabled AdmissionProgram.");

    private static SharpLinkServer CreateServer()
        => (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .Build();

    private static void ConfigureScopes(
        SharpLinkAdmissionControlOptions options,
        int limit,
        Activation activation = Activation.Unspecified)
    {
        ConfigureGlobal(options, limit, TimeSpan.FromHours(1), activation);
        options.AddContract(101, rule => rule.UseFixedWindow(rate =>
        {
            rate.PermitLimit = limit;
            rate.Window = TimeSpan.FromHours(1);
            ApplyActivation(rate, activation);
        }));
        options.AddMethod(101, 202, rule => rule.UseFixedWindow(rate =>
        {
            rate.PermitLimit = limit;
            rate.Window = TimeSpan.FromHours(1);
            ApplyActivation(rate, activation);
        }));
    }

    internal static void ConfigureGlobal(
        SharpLinkAdmissionControlOptions options,
        int limit,
        TimeSpan window,
        Activation activation = Activation.Unspecified)
        => options.Global.UseFixedWindow(rate =>
        {
            rate.PermitLimit = limit;
            rate.Window = window;
            ApplyActivation(rate, activation);
        });

    internal static void ApplyActivation(
        SharpLinkFixedWindowLimitOptions rate,
        Activation activation)
    {
        if (activation == Activation.Unspecified)
            return;
        var property = rate.GetType().GetProperty(
            "UpdateActivation",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (property is null)
            return;
        var value = Enum.Parse(
            Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType,
            activation == Activation.Immediate ? "Immediate" : "NextWindowBoundary");
        property.SetValue(rate, value);
    }

    internal enum Activation
    {
        Unspecified,
        Immediate,
        NextWindow
    }
}

[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class Issue410CleanFinalDirectBenchmarks
{
    private AdmissionRateState _permit = null!;
    private AdmissionRateState _reject = null!;

    [GlobalSetup]
    public void Setup()
    {
        _permit = Create(1_000_000_000);
        _reject = Create(1);
        using var consumed = _reject.AttemptAcquire(1);
        if (!consumed.IsAcquired)
            throw new InvalidOperationException("Failed to exhaust reject state.");
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

    private static AdmissionRateState Create(int limit)
    {
        var rule = new SharpLinkAdmissionRuleOptions();
        rule.UseFixedWindow(rate =>
        {
            rate.PermitLimit = limit;
            rate.Window = TimeSpan.FromHours(1);
        });
        return AdmissionRateState.Create(rule, TimeProvider.System);
    }
}

[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class Issue410CleanFinalUpdateBenchmarks
{
    private SharpLinkServer _immediateServer = null!;
    private SharpLinkServer _nextServer = null!;
    private SharpLinkServer _windowServer = null!;
    private SharpLinkServer _multiScopeServer = null!;
    private ISharpLinkServer _immediate = null!;
    private ISharpLinkServer _next = null!;
    private ISharpLinkServer _window = null!;
    private ISharpLinkServer _multiScope = null!;
    private int _immediateToggle;
    private int _nextToggle;
    private int _windowToggle;
    private int _multiScopeToggle;

    [GlobalSetup]
    public void Setup()
    {
        _immediateServer = CreateServer();
        _immediate = (ISharpLinkServer)_immediateServer;
        _immediate.EnableAdmissionControl(options =>
            Issue410CleanFinalControllerBenchmarks.ConfigureGlobal(
                options, 1_000_000_000, TimeSpan.FromHours(1)));

        _nextServer = CreateServer();
        _next = (ISharpLinkServer)_nextServer;
        _next.EnableAdmissionControl(options =>
            Issue410CleanFinalControllerBenchmarks.ConfigureGlobal(
                options, 1_000_000_000, TimeSpan.FromHours(1)));

        _windowServer = CreateServer();
        _window = (ISharpLinkServer)_windowServer;
        _window.EnableAdmissionControl(options =>
            Issue410CleanFinalControllerBenchmarks.ConfigureGlobal(
                options, 1_000_000_000, TimeSpan.FromHours(1)));

        _multiScopeServer = CreateServer();
        _multiScope = (ISharpLinkServer)_multiScopeServer;
        _multiScope.EnableAdmissionControl(options => ConfigureScopes(options, 1_000_000_000));
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _immediateServer.DisposeAsync();
        await _nextServer.DisposeAsync();
        await _windowServer.DisposeAsync();
        await _multiScopeServer.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public void ImmediateLimitOnly()
    {
        var limit = (Interlocked.Increment(ref _immediateToggle) & 1) == 0
            ? 999_000_000
            : 1_000_000_000;
        _immediate.UpdateAdmissionControl(options =>
            Issue410CleanFinalControllerBenchmarks.ConfigureGlobal(
                options,
                limit,
                TimeSpan.FromHours(1),
                Issue410CleanFinalControllerBenchmarks.Activation.Immediate));
    }

    [Benchmark]
    public void NextWindowLimitOnly()
    {
        var limit = (Interlocked.Increment(ref _nextToggle) & 1) == 0
            ? 999_000_000
            : 1_000_000_000;
        _next.UpdateAdmissionControl(options =>
            Issue410CleanFinalControllerBenchmarks.ConfigureGlobal(
                options,
                limit,
                TimeSpan.FromHours(1),
                Issue410CleanFinalControllerBenchmarks.Activation.NextWindow));
    }

    [Benchmark]
    public void NextWindowWithWindowChange()
    {
        var window = (Interlocked.Increment(ref _windowToggle) & 1) == 0
            ? TimeSpan.FromMinutes(55)
            : TimeSpan.FromHours(1);
        _window.UpdateAdmissionControl(options =>
            Issue410CleanFinalControllerBenchmarks.ConfigureGlobal(
                options,
                1_000_000_000,
                window,
                Issue410CleanFinalControllerBenchmarks.Activation.NextWindow));
    }

    [Benchmark]
    public void MultiScopeImmediateLimitOnly()
    {
        var limit = (Interlocked.Increment(ref _multiScopeToggle) & 1) == 0
            ? 999_000_000
            : 1_000_000_000;
        _multiScope.UpdateAdmissionControl(options => ConfigureScopes(options, limit));
    }

    private static void ConfigureScopes(SharpLinkAdmissionControlOptions options, int limit)
    {
        Issue410CleanFinalControllerBenchmarks.ConfigureGlobal(
            options,
            limit,
            TimeSpan.FromHours(1),
            Issue410CleanFinalControllerBenchmarks.Activation.Immediate);
        options.AddContract(101, rule => rule.UseFixedWindow(rate =>
        {
            rate.PermitLimit = limit;
            rate.Window = TimeSpan.FromHours(1);
            Issue410CleanFinalControllerBenchmarks.ApplyActivation(
                rate,
                Issue410CleanFinalControllerBenchmarks.Activation.Immediate);
        }));
        options.AddMethod(101, 202, rule => rule.UseFixedWindow(rate =>
        {
            rate.PermitLimit = limit;
            rate.Window = TimeSpan.FromHours(1);
            Issue410CleanFinalControllerBenchmarks.ApplyActivation(
                rate,
                Issue410CleanFinalControllerBenchmarks.Activation.Immediate);
        }));
    }

    private static SharpLinkServer CreateServer()
        => (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .Build();
}
