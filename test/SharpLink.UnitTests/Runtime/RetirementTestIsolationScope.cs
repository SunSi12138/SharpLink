namespace SharpLink.UnitTests.Runtime;

// Only the retirement test Run helper uses this scope. It owns the actual permit,
// snapshots and fixture closures; retained scopes keep those closures reachable.
internal sealed class RetirementTestIsolationScope
{
    internal const string CodecVariable = "SHARPLINK_ROLLBACK_DISABLE_CODEC";
    internal const string ContractVariable = "SHARPLINK_ROLLBACK_RETIREMENT_CONTRACT";
    private static readonly List<RetirementTestIsolationScope> Retained = [];
    private readonly SemaphoreSlim _isolation;
    private readonly TimeSpan _bound;
    private readonly bool _enableCodec;
    private readonly bool _withContract;
    private readonly Func<Task> _body;
    private readonly Func<Task> _cleanup;
    private readonly Func<bool> _ownersEnded;
    private readonly Func<string, string?> _read;
    private readonly Action<string, string?> _write;
    private readonly Action _release;
    private readonly Action<RetirementTestIsolationScope> _retain;
    private readonly List<Exception> _failures = [];
    private string? _codecSnapshot;
    private string? _contractSnapshot;
    private bool _codecAttempted;
    private bool _contractAttempted;
    private int _started;
    internal bool PermitReleased { get; private set; }
    internal bool IsRetained { get; private set; }

    internal RetirementTestIsolationScope(SemaphoreSlim isolation, TimeSpan bound,
        bool enableCodec, bool withContract, Func<Task> body, Func<Task> cleanup,
        Func<bool> ownersEnded, Func<string, string?>? read = null,
        Action<string, string?>? write = null, Action? release = null,
        Action<RetirementTestIsolationScope>? retain = null)
    {
        _isolation = isolation; _bound = bound;
        _enableCodec = enableCodec; _withContract = withContract;
        _body = body; _cleanup = cleanup; _ownersEnded = ownersEnded;
        _read = read ?? Environment.GetEnvironmentVariable;
        _write = write ?? Environment.SetEnvironmentVariable;
        _release = release ?? (() => { _isolation.Release(); });
        _retain = retain ?? (scope => { lock (Retained) Retained.Add(scope); });
    }

    internal async Task ExecuteAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("Retirement test isolation scope is single-use.");
        var failures = _failures;
        // A timeout cancels the actual semaphore admission, not a wrapper task.
        if (!await _isolation.WaitAsync(_bound).ConfigureAwait(false))
            throw new TimeoutException("Retirement test isolation admission timed out.");
        try
        {
            _codecSnapshot = _read(CodecVariable);
            _contractSnapshot = _read(ContractVariable);
            // Both snapshots exist before either write. Mark before invocation:
            // a setter may mutate and then fail; that variable still needs restore.
            _contractAttempted = true;
            _write(ContractVariable, _withContract ? "1" : null);
            _codecAttempted = true;
            _write(CodecVariable, _enableCodec ? null : "1");
            await _body().ConfigureAwait(false);
        }
        catch (Exception failure) { failures.Add(failure); }
        finally
        {
            try { await _cleanup().ConfigureAwait(false); }
            catch (Exception failure) { failures.Add(failure); }
            bool ended = false;
            try { ended = _ownersEnded(); }
            catch (Exception failure) { failures.Add(failure); }
            if (ended)
            {
                bool restored = true;
                if (_codecAttempted)
                {
                    try { _write(CodecVariable, _codecSnapshot); }
                    catch (Exception failure) { restored = false; failures.Add(failure); }
                }
                if (_contractAttempted)
                {
                    try { _write(ContractVariable, _contractSnapshot); }
                    catch (Exception failure) { restored = false; failures.Add(failure); }
                }
                if (restored)
                {
                    try { _release(); PermitReleased = true; }
                    catch (Exception failure) { failures.Add(failure); }
                }
            }
            if (!PermitReleased)
            {
                // Never release shared isolation on unknown owners OR unknown
                // environment restoration. Root the scope and its actual fixture.
                IsRetained = true;
                _retain(this);
                if (failures.Count == 0)
                    failures.Add(new InvalidOperationException("Retirement test owners remain retained."));
            }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }
}
