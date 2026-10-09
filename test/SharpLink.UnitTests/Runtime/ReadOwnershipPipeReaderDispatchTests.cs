using System.IO.Pipelines;
using System.Threading.Tasks.Sources;

namespace SharpLink.UnitTests.Runtime;

/// <summary>Ordinary await dispatch must remain reusable without inheriting producer ownership.</summary>
public class ReadOwnershipPipeReaderDispatchTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConsumerChildrenMustNotAttachToThePublishingTask(bool compilerAwait)
    {
        var inner = new ControlledReader();
        var reader = new ReadOwnershipPipeReader(inner);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? child = null;
        int? callbackTask = null;
        var consumer = compilerAwait ? ConsumeAsync() : RegisterRaw();
        var producer = Task.Factory.StartNew(inner.Publish, CancellationToken.None,
            TaskCreationOptions.None, TaskScheduler.Default);
        await producer.WaitAsync(Timeout);
        await consumer.WaitAsync(Timeout);
        try
        {
            await child!.WaitAsync(Timeout);
        }
        catch (ApplicationException)
        {
        }
        Ensure(producer.IsCompletedSuccessfully, "consumer children must not fault the publishing Task");
        Ensure(child!.IsFaulted, "the attached-child failure must actually have occurred");
        Ensure(callbackTask is null, "the ordinary callback must not inherit the producer's Task identity");

        Task RegisterRaw()
        {
            var read = reader.ReadAsync();
            read.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() => Consume(read.GetAwaiter().GetResult()));
            return observed.Task;
        }

        async Task ConsumeAsync() => Consume(await reader.ReadAsync().ConfigureAwait(false));

        void Consume(ReadResult result)
        {
            callbackTask = Task.CurrentId;
            reader.AdvanceTo(result.Buffer.End);
            child = Task.Factory.StartNew(static () => throw new ApplicationException("consumer child"),
                CancellationToken.None, TaskCreationOptions.AttachedToParent, TaskScheduler.Default);
            observed.SetResult();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DispatchPolicyMustBeCapturedWhenTheReadIsArmed(bool asynchronous)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var inner = new ControlledReader();
                var reader = new ReadOwnershipPipeReader(inner) { RunContinuationsAsynchronously = asynchronous };
                var read = reader.ReadAsync();
                reader.RunContinuationsAsynchronously = !asynchronous;
                var publishingThread = Environment.CurrentManagedThreadId;
                read.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() =>
                {
                    try
                    {
                        var result = read.GetAwaiter().GetResult();
                        reader.AdvanceTo(result.Buffer.End);
                        Ensure((Environment.CurrentManagedThreadId != publishingThread) == asynchronous,
                            "changing the property must not change an already armed read's dispatch policy");
                        completed.SetResult();
                    }
                    catch (Exception error)
                    {
                        completed.SetException(error);
                    }
                });
                inner.Publish();
            }
            catch (Exception error)
            {
                completed.TrySetException(error);
            }
        });
        thread.Start();
        try
        {
            await completed.Task.WaitAsync(Timeout);
        }
        finally
        {
            Ensure(thread.Join(Timeout), "the dedicated publisher must exit");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OrdinaryQueuedRegistrationMustNotAllocateOnTheRegisteringThread(bool late)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try
            {
                MeasureQueuedRegistration(late);
                completed.SetResult();
            }
            catch (Exception error)
            {
                completed.SetException(error);
            }
        }, state: (object?)null, preferLocal: false);
        await completed.Task.WaitAsync(Timeout);
    }

    private static void MeasureQueuedRegistration(bool late)
    {
        Ensure(Task.CurrentId is null && SynchronizationContext.Current is null,
            "the measurement requires an ordinary pool callback, not a producer Task or custom context");
        var inner = new ControlledReader();
        var reader = new ReadOwnershipPipeReader(inner) { RunContinuationsAsynchronously = !late };
        using var done = new ManualResetEventSlim();
        // Fast warmup waits can all finish while spinning. Force this fixture's lazy blocking
        // lock to initialize before measurement, rather than charge its first 24 bytes to a read.
        Ensure(!done.Wait(TimeSpan.FromMilliseconds(1)), "the unused fixture signal must remain unsignaled");
        ValueTask<ReadResult> read = default;
        Exception? error = null;
        var consumed = 0;
        Action continuation = () =>
        {
            try
            {
                var result = read.GetAwaiter().GetResult();
                reader.AdvanceTo(result.Buffer.End);
                consumed++;
            }
            catch (Exception exception)
            {
                error = exception;
            }
            finally
            {
                done.Set();
            }
        };
        for (var i = 0; i < 1_000; i++) Round();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++) Round();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Ensure(error is null, $"the queued consumer failed: {error}");
        Ensure(consumed == 11_000, "every queued consumer must run exactly once");
        // This counter deliberately covers registration/publication only. Worker-side and
        // process-wide allocation are measured separately by the controlled performance harness.
        Ensure(bytes == 0, $"ordinary queued registration must allocate 0 bytes on its registering thread, got {bytes}");

        void Round()
        {
            done.Reset();
            read = reader.ReadAsync();
            if (late) inner.Publish();
            read.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(continuation);
            if (!late) inner.Publish();
            Ensure(done.Wait(Timeout), "queued registration must dispatch without a lost wakeup");
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ControlledReader : PipeReader, IValueTaskSource<ReadResult>
    {
        private Action<object?>? _callback;
        private object? _state;
        private short _version;
        private bool _completed;

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            _completed = false;
            return new ValueTask<ReadResult>(this, unchecked(++_version));
        }

        internal void Publish()
        {
            _completed = true;
            var callback = _callback;
            var state = _state;
            _callback = null;
            _state = null;
            callback!(state);
        }

        public ReadResult GetResult(short token)
        {
            Ensure(token == _version && _completed, "the inner read must complete before observation");
            return new ReadResult(default, false, false);
        }

        public ValueTaskSourceStatus GetStatus(short token)
            => _completed ? ValueTaskSourceStatus.Succeeded : ValueTaskSourceStatus.Pending;

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            _callback = continuation;
            _state = state;
        }

        public override void AdvanceTo(SequencePosition consumed) { }
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) { }
        public override void CancelPendingRead() { }
        public override void Complete(Exception? exception = null) { }
        public override bool TryRead(out ReadResult result) { result = default; return false; }
    }
}
