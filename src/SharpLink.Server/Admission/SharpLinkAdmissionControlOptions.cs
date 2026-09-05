namespace SharpLink.Server;

/// <summary>Configures a concurrency limit owned by one admission scope.</summary>
public sealed class SharpLinkConcurrencyLimitOptions
{
    /// <summary>Gets or sets the maximum simultaneously active calls.</summary>
    public int PermitLimit { get; set; }

    internal void Validate()
        => ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PermitLimit);
}

/// <summary>Configures a token-bucket request-rate limit.</summary>
public sealed class SharpLinkTokenBucketLimitOptions
{
    /// <summary>Gets or sets the maximum token capacity.</summary>
    public int TokenLimit { get; set; }
    /// <summary>Gets or sets the tokens added per replenishment period.</summary>
    public int TokensPerPeriod { get; set; }
    /// <summary>Gets or sets the automatic replenishment period, up to 2,147,483,647 milliseconds.</summary>
    public TimeSpan ReplenishmentPeriod { get; set; } = TimeSpan.FromSeconds(1);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TokenLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TokensPerPeriod);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ReplenishmentPeriod, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ReplenishmentPeriod, SharpLinkTimer.MaximumDelay);
    }
}

/// <summary>Configures a fixed-window request-rate limit.</summary>
public sealed class SharpLinkFixedWindowLimitOptions
{
    /// <summary>Gets or sets the maximum calls admitted during one window.</summary>
    public int PermitLimit { get; set; }
    /// <summary>Gets or sets the fixed window duration, up to 2,147,483,647 milliseconds.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(1);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PermitLimit);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Window, SharpLinkTimer.MaximumDelay);
    }
}

/// <summary>Configures a segmented sliding-window request-rate limit.</summary>
public sealed class SharpLinkSlidingWindowLimitOptions
{
    /// <summary>Gets or sets the maximum calls admitted during one sliding window.</summary>
    public int PermitLimit { get; set; }
    /// <summary>Gets or sets the complete sliding window duration, up to 2,147,483,647 milliseconds.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>Gets or sets the number of replenishment segments in each window.</summary>
    public int SegmentsPerWindow { get; set; } = 4;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PermitLimit);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Window, SharpLinkTimer.MaximumDelay);
        ArgumentOutOfRangeException.ThrowIfLessThan(SegmentsPerWindow, 2);
        if (Window.Ticks < SegmentsPerWindow)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SegmentsPerWindow),
                "Each sliding-window segment must span at least one TimeSpan tick.");
        }
    }
}

/// <summary>Configures concurrency and at most one request-rate policy for an admission scope.</summary>
public class SharpLinkAdmissionRuleOptions
{
    /// <summary>Gets the optional concurrency limit.</summary>
    public SharpLinkConcurrencyLimitOptions? Concurrency { get; private set; }

    internal object? RateLimit { get; private set; }

    /// <summary>Sets the maximum active calls for this scope.</summary>
    /// <param name="permitLimit">A positive maximum number of calls holding this scope.</param>
    /// <returns>This rule.</returns>
    public SharpLinkAdmissionRuleOptions UseConcurrency(int permitLimit)
    {
        var options = new SharpLinkConcurrencyLimitOptions { PermitLimit = permitLimit };
        options.Validate();
        Concurrency = options;
        return this;
    }

    /// <summary>Sets a token-bucket request-rate policy for this scope.</summary>
    /// <param name="configure">Configures token capacity and replenishment.</param>
    /// <returns>This rule.</returns>
    public SharpLinkAdmissionRuleOptions UseTokenBucket(Action<SharpLinkTokenBucketLimitOptions> configure)
        => SetRateLimit(configure, static () => new SharpLinkTokenBucketLimitOptions());

    /// <summary>Sets a fixed-window request-rate policy for this scope.</summary>
    /// <param name="configure">Configures window capacity and duration.</param>
    /// <returns>This rule.</returns>
    public SharpLinkAdmissionRuleOptions UseFixedWindow(Action<SharpLinkFixedWindowLimitOptions> configure)
        => SetRateLimit(configure, static () => new SharpLinkFixedWindowLimitOptions());

    /// <summary>Sets a sliding-window request-rate policy for this scope.</summary>
    /// <param name="configure">Configures capacity, duration and segment count.</param>
    /// <returns>This rule.</returns>
    public SharpLinkAdmissionRuleOptions UseSlidingWindow(Action<SharpLinkSlidingWindowLimitOptions> configure)
        => SetRateLimit(configure, static () => new SharpLinkSlidingWindowLimitOptions());

    internal bool HasLimit => Concurrency is not null || RateLimit is not null;

    internal void CopyLimitsTo(SharpLinkAdmissionRuleOptions destination)
    {
        destination.Concurrency = Concurrency is null
            ? null
            : new SharpLinkConcurrencyLimitOptions { PermitLimit = Concurrency.PermitLimit };
        destination.RateLimit = RateLimit switch
        {
            SharpLinkTokenBucketLimitOptions source => new SharpLinkTokenBucketLimitOptions
            {
                TokenLimit = source.TokenLimit,
                TokensPerPeriod = source.TokensPerPeriod,
                ReplenishmentPeriod = source.ReplenishmentPeriod
            },
            SharpLinkFixedWindowLimitOptions source => new SharpLinkFixedWindowLimitOptions
            {
                PermitLimit = source.PermitLimit,
                Window = source.Window
            },
            SharpLinkSlidingWindowLimitOptions source => new SharpLinkSlidingWindowLimitOptions
            {
                PermitLimit = source.PermitLimit,
                Window = source.Window,
                SegmentsPerWindow = source.SegmentsPerWindow
            },
            _ => null
        };
    }

    internal void Validate()
    {
        Concurrency?.Validate();
        switch (RateLimit)
        {
            case SharpLinkTokenBucketLimitOptions tokenBucket:
                tokenBucket.Validate();
                break;
            case SharpLinkFixedWindowLimitOptions fixedWindow:
                fixedWindow.Validate();
                break;
            case SharpLinkSlidingWindowLimitOptions slidingWindow:
                slidingWindow.Validate();
                break;
        }
    }

    internal SharpLinkAdmissionRuleOptions CloneRuleValidated()
    {
        Validate();
        var clone = new SharpLinkAdmissionRuleOptions();
        CopyLimitsTo(clone);
        return clone;
    }

    private SharpLinkAdmissionRuleOptions SetRateLimit<T>(Action<T> configure, Func<T> factory)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (RateLimit is not null)
            throw new InvalidOperationException("Only one request-rate policy may be configured for an admission scope.");
        var options = factory();
        configure(options);
        RateLimit = options;
        Validate();
        return this;
    }
}

/// <summary>Configures a bounded, opportunistically reclaimed partition admission layer.</summary>
public sealed class SharpLinkPartitionAdmissionOptions : SharpLinkAdmissionRuleOptions
{
    /// <summary>Gets or sets the maximum number of live partition entries.</summary>
    public int MaxPartitions { get; set; } = 1024;
    /// <summary>Gets or sets how long an unused partition must remain idle before reclamation.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    internal new void Validate()
    {
        base.Validate();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPartitions);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(IdleTimeout, TimeSpan.Zero);
        if (!HasLimit)
            throw new InvalidOperationException("A partition selector requires at least one partition limit.");
    }

    internal SharpLinkPartitionAdmissionOptions CloneValidated()
    {
        Validate();
        var clone = new SharpLinkPartitionAdmissionOptions
        {
            MaxPartitions = MaxPartitions,
            IdleTimeout = IdleTimeout
        };
        CopyLimitsTo(clone);
        return clone;
    }
}

/// <summary>Read-only data supplied to a synchronous admission partition selector.</summary>
public sealed class SharpLinkAdmissionContext
{
    internal SharpLinkAdmissionContext(
        long contractId,
        long methodId,
        RpcMethodKind methodKind,
        string connectionId,
        SharpLinkAuthenticationContext? authenticationContext,
        SharpLinkMetadata? metadata)
    {
        ContractId = contractId;
        MethodId = methodId;
        MethodKind = methodKind;
        ConnectionId = connectionId;
        AuthenticationContext = authenticationContext;
        Metadata = metadata;
    }

    /// <summary>Gets the stable generated contract ID.</summary>
    public long ContractId { get; }
    /// <summary>Gets the stable generated method ID.</summary>
    public long MethodId { get; }
    /// <summary>Gets the generated RPC invocation shape.</summary>
    public RpcMethodKind MethodKind { get; }
    /// <summary>Gets the physical connection ID.</summary>
    public string ConnectionId { get; }
    /// <summary>Gets the authenticated peer context, when authentication is enabled.</summary>
    public SharpLinkAuthenticationContext? AuthenticationContext { get; }
    /// <summary>Gets request metadata, or <see langword="null"/> when absent.</summary>
    public SharpLinkMetadata? Metadata { get; }
}

/// <summary>Configures optional active admission control for one SharpLink server.</summary>
/// <example>
/// <code>
/// builder.UseAdmissionControl(options =&gt;
/// {
///     options.Global.UseConcurrency(256);
///     options.MaxQueuedCalls = 512;
///     options.MaxQueuedBytes = 16 * 1024 * 1024;
///     options.MaxQueueDelay = TimeSpan.FromSeconds(2);
///     options.AddMethod&lt;IOrders&gt;(nameof(IOrders.SubmitAsync), rule =&gt;
///         rule.UseTokenBucket(rate =&gt;
///         {
///             rate.TokenLimit = 1000;
///             rate.TokensPerPeriod = 1000;
///             rate.ReplenishmentPeriod = TimeSpan.FromSeconds(1);
///         }));
/// });
/// </code>
/// </example>
public sealed class SharpLinkAdmissionControlOptions
{
    private readonly List<AdmissionRuleRegistration> _rules = [];
    private Func<SharpLinkAdmissionContext, string?>? _partitionSelector;
    private SharpLinkPartitionAdmissionOptions? _partition;

    /// <summary>Gets the global admission rule evaluated before narrower rules.</summary>
    public SharpLinkAdmissionRuleOptions Global { get; } = new();
    /// <summary>Gets or sets the maximum queued call count. Zero disables waiting.</summary>
    public int MaxQueuedCalls { get; set; }
    /// <summary>Gets or sets the maximum total bytes retained by queued calls. Zero disables waiting.</summary>
    public long MaxQueuedBytes { get; set; }
    /// <summary>
    /// Gets or sets the maximum time one call may remain queued, up to 2,147,483,647 milliseconds.
    /// Zero disables waiting.
    /// </summary>
    public TimeSpan MaxQueueDelay { get; set; }
    /// <summary>Gets or sets whether OneWay calls may wait instead of being dropped immediately.</summary>
    public bool QueueOneWayCalls { get; set; }

    /// <summary>Adds a rule resolved from a generated contract type at server build time.</summary>
    /// <typeparam name="TContract">A generated RPC contract.</typeparam>
    /// <param name="configure">Configures limits for the contract.</param>
    /// <returns>This options object.</returns>
    public SharpLinkAdmissionControlOptions AddContract<TContract>(Action<SharpLinkAdmissionRuleOptions> configure)
        where TContract : class, IService
        => AddRule(new AdmissionRuleRegistration(typeof(TContract), null, null, null, ConfigureRule(configure)));

    /// <summary>Adds a rule for a stable generated contract ID.</summary>
    /// <param name="contractId">The nonzero generated contract ID.</param>
    /// <param name="configure">Configures limits for the contract.</param>
    /// <returns>This options object.</returns>
    public SharpLinkAdmissionControlOptions AddContract(long contractId, Action<SharpLinkAdmissionRuleOptions> configure)
    {
        if (contractId == 0)
            throw new ArgumentOutOfRangeException(nameof(contractId));
        return AddRule(new AdmissionRuleRegistration(null, contractId, null, null, ConfigureRule(configure)));
    }

    /// <summary>Adds a method rule resolved from generated contract type and method name at build time.</summary>
    /// <typeparam name="TContract">A generated RPC contract.</typeparam>
    /// <param name="methodName">The unique source method name in the generated contract.</param>
    /// <param name="configure">Configures limits for the method.</param>
    /// <returns>This options object.</returns>
    public SharpLinkAdmissionControlOptions AddMethod<TContract>(
        string methodName,
        Action<SharpLinkAdmissionRuleOptions> configure)
        where TContract : class, IService
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        return AddRule(new AdmissionRuleRegistration(
            typeof(TContract), null, methodName, null, ConfigureRule(configure)));
    }

    /// <summary>Adds a method rule for stable generated contract and method IDs.</summary>
    /// <param name="contractId">The nonzero generated contract ID.</param>
    /// <param name="methodId">The nonzero generated method ID.</param>
    /// <param name="configure">Configures limits for the method.</param>
    /// <returns>This options object.</returns>
    public SharpLinkAdmissionControlOptions AddMethod(
        long contractId,
        long methodId,
        Action<SharpLinkAdmissionRuleOptions> configure)
    {
        if (contractId == 0)
            throw new ArgumentOutOfRangeException(nameof(contractId));
        if (methodId == 0)
            throw new ArgumentOutOfRangeException(nameof(methodId));
        return AddRule(new AdmissionRuleRegistration(null, contractId, null, methodId, ConfigureRule(configure)));
    }

    /// <summary>Adds a bounded partition layer selected synchronously from call context.</summary>
    /// <param name="selector">Returns a stable partition key; null or empty selects the default partition.</param>
    /// <param name="configure">Configures partition bounds and per-partition limits.</param>
    /// <returns>This options object.</returns>
    public SharpLinkAdmissionControlOptions UsePartition(
        Func<SharpLinkAdmissionContext, string?> selector,
        Action<SharpLinkPartitionAdmissionOptions> configure)
    {
        if (_partition is not null)
            throw new InvalidOperationException("Only one partition layer may be configured.");
        _partitionSelector = selector ?? throw new ArgumentNullException(nameof(selector));
        ArgumentNullException.ThrowIfNull(configure);
        var partition = new SharpLinkPartitionAdmissionOptions();
        configure(partition);
        partition.Validate();
        _partition = partition;
        return this;
    }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaxQueuedCalls);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxQueuedBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxQueueDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxQueueDelay, SharpLinkTimer.MaximumDelay);
        Global.Validate();
        foreach (var registration in _rules)
            registration.Rule.Validate();
        _partition?.Validate();
        var queueEnabled = MaxQueuedCalls != 0 || MaxQueuedBytes != 0 || MaxQueueDelay != TimeSpan.Zero;
        if (queueEnabled && (MaxQueuedCalls == 0 || MaxQueuedBytes == 0 || MaxQueueDelay == TimeSpan.Zero))
        {
            throw new InvalidOperationException(
                "MaxQueuedCalls, MaxQueuedBytes, and MaxQueueDelay must all be positive to enable waiting.");
        }
        if (!Global.HasLimit && _rules.Count == 0 && _partition is null)
            throw new InvalidOperationException("Admission control requires at least one configured limit.");
    }

    internal IReadOnlyList<AdmissionRuleRegistration> Rules => _rules;
    internal Func<SharpLinkAdmissionContext, string?>? PartitionSelector => _partitionSelector;
    internal SharpLinkPartitionAdmissionOptions? Partition => _partition;

    /// <summary>Validates and deep-copies every mutable admission option for one build plan.</summary>
    internal SharpLinkAdmissionControlOptions CloneValidated()
    {
        Validate();
        var clone = new SharpLinkAdmissionControlOptions
        {
            MaxQueuedCalls = MaxQueuedCalls,
            MaxQueuedBytes = MaxQueuedBytes,
            MaxQueueDelay = MaxQueueDelay,
            QueueOneWayCalls = QueueOneWayCalls,
            _partitionSelector = _partitionSelector,
            _partition = _partition?.CloneValidated()
        };
        Global.CopyLimitsTo(clone.Global);
        foreach (var registration in _rules)
        {
            clone._rules.Add(new AdmissionRuleRegistration(
                registration.ContractType,
                registration.ContractId,
                registration.MethodName,
                registration.MethodId,
                registration.Rule.CloneRuleValidated()));
        }
        return clone;
    }

    private SharpLinkAdmissionControlOptions AddRule(AdmissionRuleRegistration registration)
    {
        if (!registration.Rule.HasLimit)
            throw new InvalidOperationException("An admission rule requires at least one configured limit.");
        _rules.Add(registration);
        return this;
    }

    private static SharpLinkAdmissionRuleOptions ConfigureRule(Action<SharpLinkAdmissionRuleOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var rule = new SharpLinkAdmissionRuleOptions();
        configure(rule);
        rule.Validate();
        return rule;
    }
}

internal sealed record AdmissionRuleRegistration(
    Type? ContractType,
    long? ContractId,
    string? MethodName,
    long? MethodId,
    SharpLinkAdmissionRuleOptions Rule);
