namespace SharpLink.Runtime;

public sealed class PooledAsyncStreamDispatcher<T> :
    IStreamDispatcher,
    IAsyncEnumerable<T>,
    IAsyncEnumerator<T>,
    IValueTaskSource<bool>
{
    private static readonly ConcurrentBag<PooledAsyncStreamDispatcher<T>> Pool = [];

    // 仅用于 WaitSource 的一致性（不是热路径锁）
    private readonly Lock _waitGate = new();


    private ManualResetValueTaskSourceCore<bool> _waitSource;

    // SPSC ring buffer: _head 仅消费者写，_tail 仅生产者写
    // 双方会读对方的值，因此使用 Volatile.Read/Write 保证可见性与顺序
    private T[] _buffer = new T[16]; // 2 的幂
    private int _mask;               // _buffer.Length - 1
    private int _head;               // consumer owned
    private int _tail;               // producer owned

    // 扩容握手：避免生产者在换 buffer 时消费者正在读老数组
    // 0 = 正常，1 = 扩容中（生产者设1，完成后设0）
    private int _resizeInProgress;

    // 消费者是否正在执行 dequeue 临界段（扩容握手用）
    private int _consumerInDequeue;

    // 0 = 无信号，1 = 有信号（WaitForData 的快路径）
    private int _signalState;

    // 0 = 没 waiter，1 = 有 waiter（Interlocked 管理，避免混用同步手段）
    private int _waiterState;

    // 重要状态：用 Volatile 读写对称（审核 #3）
    private bool _completed;
    private bool _disposed;

    // GetAsyncEnumerator 原子防御（审核 #4）：0/1
    private int _enumeratorTaken;

    private bool _inPool;
    private Exception? _error;

    private CancellationToken _enumerationToken;
    private CancellationTokenRegistration _enumerationCancellationRegistration;

    private T? _current;

    private const int InitialCapacity = 16;
    private const int ShrinkThreshold = 4096;

    private PooledAsyncStreamDispatcher()
    {
        _waitSource = new ManualResetValueTaskSourceCore<bool>
        {
            RunContinuationsAsynchronously = true
        };
        _mask = _buffer.Length - 1;
    }

    public static PooledAsyncStreamDispatcher<T> Rent(CancellationToken enumerationToken = default)
    {
        if (!Pool.TryTake(out var dispatcher))
            dispatcher = new PooledAsyncStreamDispatcher<T>();

        dispatcher.Reset(enumerationToken);
        return dispatcher;
    }

    private void Reset(CancellationToken enumerationToken)
    {
        _enumerationCancellationRegistration.Dispose();

        _enumerationToken = enumerationToken;

        _waitSource = new ManualResetValueTaskSourceCore<bool>
        {
            RunContinuationsAsynchronously = true
        };

        // 可选：避免长期占用超大数组
        if (_buffer.Length > ShrinkThreshold)
        {
            _buffer = new T[InitialCapacity];
            _mask = _buffer.Length - 1;
        }

        _head = 0;
        _tail = 0;

        Volatile.Write(ref _signalState, 0);
        Volatile.Write(ref _waiterState, 0);
        Volatile.Write(ref _resizeInProgress, 0);
        Volatile.Write(ref _consumerInDequeue, 0);

        Volatile.Write(ref _completed, false);
        Volatile.Write(ref _disposed, false);

        Volatile.Write(ref _enumeratorTaken, 0);
        _inPool = false;

        _error = null;
        _current = default;
    }

    public ValueTask DispatchAsync(ReadOnlySequence<byte> payload)
    {

        // 反序列化可能很重：尽量让队列/锁不要挡它（这里本来就无锁）
        var item = RpcCodec.Deserialize<T>(payload);

        // 生产者侧：如果已结束/已释放，直接丢弃（读用 Volatile 对称）
        if (Volatile.Read(ref _completed) || Volatile.Read(ref _disposed))
            return ValueTask.CompletedTask;

        // 快路径：尝试写入 ring
        if (!TryEnqueue(item!))
        {
            // 满了：扩容（慢路径）
            GrowAndEnqueue(item!);
        }

        // 审核 #2：扩容路径不再内部 Signal，统一在外层一次
        Signal();
        return ValueTask.CompletedTask;
    }

    public void Complete(bool isError, string? errorMessage)
    {
        var message = string.IsNullOrWhiteSpace(errorMessage) ? "Remote Error" : errorMessage;
        Complete(isError
            ? SharpLinkException.TryParsePayloadMessage(message, out var structuredException)
                ? structuredException
                : new SharpLinkException(SharpLinkErrorCode.RemoteError, message)
            : null);
    }

    public void Complete(Exception? exception)
    {
        // 已完成则忽略
        if (Volatile.Read(ref _completed))
            return;

        // 审核 #1：先写 error，最后发布 completed（release）
        _error = exception;

        Volatile.Write(ref _completed, true);
        Signal();
    }

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        // 审核 #4：原子防御，避免极端并发双进入
        if (Interlocked.CompareExchange(ref _enumeratorTaken, 1, 0) != 0)
            throw new InvalidOperationException("Only single consumer is supported.");

        if (cancellationToken.CanBeCanceled)
            _enumerationToken = cancellationToken;

        _enumerationCancellationRegistration = _enumerationToken.CanBeCanceled
            ? _enumerationToken.UnsafeRegister(static state => ((PooledAsyncStreamDispatcher<T>)state!).Signal(), this)
            : default;

        return this;
    }

    public T Current => _current!;

    public async ValueTask<bool> MoveNextAsync()
    {
        while (true)
        {
            _enumerationToken.ThrowIfCancellationRequested();

            // 扩容慢路径：让出一次（极少发生）
            if (Volatile.Read(ref _resizeInProgress) == 1)
                await Task.Yield();

            if (TryDequeue(out var value))
            {
                _current = value;

                // 如果已经 complete 且队列空且已 Dispose，则回收
                if (Volatile.Read(ref _completed) && IsEmpty() && Volatile.Read(ref _disposed))
                    TryReturnToPool();

                return true;
            }

            // 没取到：如果已完成则结束（并抛错误）
            if (Volatile.Read(ref _completed))
            {
                Volatile.Write(ref _disposed, true);

                var err = _error;
                TryReturnToPool();

                if (err is not null)
                    throw err;

                return false;
            }

            _enumerationToken.ThrowIfCancellationRequested();
            await WaitForDataAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        // 审核 #3：写用 Volatile.Write 对称
        Volatile.Write(ref _disposed, true);
        _current = default;

        // 安全策略：不保证生产者停止 -> 仍需 completed && empty 才回池
        TryReturnToPool();

        Signal();
        return ValueTask.CompletedTask;
    }

    public bool GetResult(short token) => _waitSource.GetResult(token);
    bool IValueTaskSource<bool>.GetResult(short token) => _waitSource.GetResult(token);

    public ValueTaskSourceStatus GetStatus(short token) => _waitSource.GetStatus(token);
    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _waitSource.GetStatus(token);

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _waitSource.OnCompleted(continuation, state, token, flags);

    void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _waitSource.OnCompleted(continuation, state, token, flags);

    // --------------------------
    // SPSC ring buffer primitives
    // --------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryEnqueue(T item)
    {
        // tail 是生产者私有写
        var tail = _tail;
        var next = (tail + 1) & _mask;

        // 读 head（消费者写），判断是否满
        var head = Volatile.Read(ref _head);
        if (next == head)
            return false;

        _buffer[tail] = item;

        // publish tail：让消费者可见（release）
        Volatile.Write(ref _tail, next);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryDequeue(out T value)
    {
        // 如果正在扩容，让上层去 Yield（慢路径）
        if (Volatile.Read(ref _resizeInProgress) == 1)
        {
            value = default!;
            return false;
        }

        // 标记进入 dequeue 临界段（扩容握手用）
        Volatile.Write(ref _consumerInDequeue, 1);

        try
        {
            // 进来后再检查一次，避免刚好遇到扩容开始
            if (Volatile.Read(ref _resizeInProgress) == 1)
            {
                value = default!;
                return false;
            }

            var head = _head;
            var tail = Volatile.Read(ref _tail);
            if (head == tail)
            {
                value = default!;
                return false;
            }

            value = _buffer[head];

            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                _buffer[head] = default!;

            // publish head：让生产者可见（release）
            Volatile.Write(ref _head, (head + 1) & _mask);
            return true;
        }
        finally
        {
            Volatile.Write(ref _consumerInDequeue, 0);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsEmpty()
        => Volatile.Read(ref _head) == Volatile.Read(ref _tail);

    private void GrowAndEnqueue(T item)
    {
        // 开始扩容：告诉消费者暂避
        Volatile.Write(ref _resizeInProgress, 1);

        // 等消费者不在 dequeue 临界段（扩容极少发生）
        var sw = new SpinWait();
        while (Volatile.Read(ref _consumerInDequeue) == 1)
            sw.SpinOnce();

        var old = _buffer;
        var oldMask = _mask;

        var head = Volatile.Read(ref _head);
        var tail = Volatile.Read(ref _tail);

        var count = tail >= head ? (tail - head) : (old.Length - head + tail);
        var newSize = old.Length << 1;
        var newBuf = new T[newSize];
        var newMask = newSize - 1;

        for (var i = 0; i < count; i++)
            newBuf[i] = old[(head + i) & oldMask];

        // 交换 buffer + 指针（消费者此时不在 dequeue 临界段）
        _buffer = newBuf;
        _mask = newMask;

        // 重置 head/tail 到新布局
        Volatile.Write(ref _head, 0);
        Volatile.Write(ref _tail, count);

        // enqueue 新元素（必有空间）
        newBuf[count] = item;
        Volatile.Write(ref _tail, (count + 1) & newMask);

        // 扩容结束
        Volatile.Write(ref _resizeInProgress, 0);

        // 审核 #2：这里不再 Signal()，由 DispatchAsync 外层统一唤醒
    }

    // --------------------------
    // Wait/Signal (async)
    // --------------------------

    private ValueTask<bool> WaitForDataAsync()
    {
        // 快路径：已有 signal
        if (Interlocked.Exchange(ref _signalState, 0) == 1)
            return ValueTask.FromResult(true);

        lock (_waitGate)
        {
            if (Interlocked.Exchange(ref _signalState, 0) == 1)
                return ValueTask.FromResult(true);

            // 单消费者下理论上不会重入，但保留防御
            if (Interlocked.CompareExchange(ref _waiterState, 1, 0) != 0)
                throw new InvalidOperationException("Only one waiter is supported.");

            if (Interlocked.Exchange(ref _signalState, 0) == 1)
            {
                Interlocked.Exchange(ref _waiterState, 0);
                return ValueTask.FromResult(true);
            }
            
            _waitSource.Reset();
            return new ValueTask<bool>(this, _waitSource.Version);
        }
    }

    private void Signal()
    {
        // 设置 signal（如果本来就有，则直接返回）
        if (Interlocked.Exchange(ref _signalState, 1) == 1)
            return;

        // 无 waiter：留给快路径
        if (Volatile.Read(ref _waiterState) == 0)
            return;

        lock (_waitGate)
        {
            if (Interlocked.Exchange(ref _waiterState, 0) == 1)
            {
                Interlocked.Exchange(ref _signalState, 0);
                _waitSource.SetResult(true);
            }
        }
    }

    // --------------------------
    // Pooling (安全策略：必须 completed && empty 才回池)
    // --------------------------

    private void TryReturnToPool()
    {
        if (_inPool)
            return;

        // 关键：不保证消费者 Dispose 后生产者停止 => 必须等 completed 才能安全回收
        if (!Volatile.Read(ref _completed) || !IsEmpty())
            return;

        _enumerationCancellationRegistration.Dispose();
        _error = null;

        // 复位枚举器占用标记
        Volatile.Write(ref _enumeratorTaken, 0);
        _current = default;

        Volatile.Write(ref _signalState, 0);
        Volatile.Write(ref _waiterState, 0);
        Volatile.Write(ref _resizeInProgress, 0);
        Volatile.Write(ref _consumerInDequeue, 0);

        // 注意：_completed/_disposed 会在下次 Reset 时统一清
        _inPool = true;
        Pool.Add(this);
    }
}
