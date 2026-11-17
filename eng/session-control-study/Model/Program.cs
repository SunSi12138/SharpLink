using System;
using System.Threading;

// 负对照：客户端旧视图未刷新，服务端按“最新注册”路由。
sealed class NotifyOnly
{
    public long ClientCachedRegistration { get; private set; } = 1;
    public long ServerRegistration { get; private set; } = 1;

    public void Replace(long next) => ServerRegistration = next;
    public void DeliverNotification() => ClientCachedRegistration = ServerRegistration;
    public long DispatchOldRequest() => ServerRegistration; // 故意展示 retarget 风险
}

// 正确性参考 gate，不是热路径优化实现（有 lock）。
sealed class AdmissionGate
{
    private readonly object _gate = new();
    private bool _open = true;
    private int _active;

    public int Active { get { lock (_gate) return _active; } }

    public IDisposable? TryEnter()
    {
        lock (_gate)
        {
            if (!_open) return null;
            ++_active;
            return new Ticket(this);
        }
    }

    public void Cut()
    {
        lock (_gate) _open = false;
    }

    private void Exit()
    {
        lock (_gate) --_active;
    }

    private sealed class Ticket : IDisposable
    {
        private AdmissionGate? _owner;
        public Ticket(AdmissionGate owner) => _owner = owner;
        public void Dispose()
        {
            AdmissionGate? owner = Interlocked.Exchange(ref _owner, null);
            owner?.Exit();
        }
    }
}

static class Program
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    public static int Main()
    {
        var negative = new NotifyOnly();
        negative.Replace(2); // 服务端已从 R1 改为 R2
        Check(negative.ClientCachedRegistration == 1,
            "client must still hold old view before notification");
        Check(negative.DispatchOldRequest() == 2,
            "negative control should demonstrate stale request retargeting");
        negative.DeliverNotification();
        Check(negative.ClientCachedRegistration == 2, "refresh should update view");

        var gate = new AdmissionGate();
        IDisposable oldCall = gate.TryEnter()
            ?? throw new InvalidOperationException("old call should be admitted before cut");
        gate.Cut();
        Check(gate.TryEnter() is null, "after cut new call must be rejected");
        Check(gate.Active == 1, "already admitted call remains alive");
        oldCall.Dispose();
        Check(gate.Active == 0, "physical owner must finally release");
        Console.WriteLine("MODEL PASS: notification-only hazard observed; admission ordering holds");
        return 0;
    }
}
