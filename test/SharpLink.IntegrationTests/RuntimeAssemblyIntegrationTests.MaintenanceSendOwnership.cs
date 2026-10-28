using System.IO.Pipelines;
using System.Net.Security;
using System.Security.Authentication;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    public async Task CooperativeMaintenanceFencesPhysicallyBlockedOldResponseOnRealTlsCohort()
    {
        await using var harness = await MaintenanceHarness.CreateAsync(gateSends: true);
        var gate = harness.SendGate!;
        gate.Arm();
        var oldCall = InvokeValueTaskAsync<int>(harness.Held[0], harness.Plugin.ContractType,
            "UnaryAsync", 7, CancellationToken.None).AsTask();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForMaintenanceAsync(() => !((SharpLinkServer)harness.Server).HasMaintenanceOwners());
            Ensure(!oldCall.IsCompleted, "old response is physically held even after server invocation owners end");
            var operation = harness.Begin();
            await WaitForMaintenanceAsync(() => operation.Stage == SharpLinkMaintenanceStage.Freezing);
            Ensure(!operation.WaitAsync().IsCompleted && harness.Plugin.GetStaticInt("Disposed") == 0,
                "ordinary FIFO control cannot overtake old response transport ownership");
            gate.Release();
            Ensure(await oldCall.WaitAsync(TimeSpan.FromSeconds(10)) == 8, "old response drains before retirement");
            var result = await operation.WaitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Ensure(result.Stage == SharpLinkMaintenanceStage.Succeeded, $"physical send joined: {result.Failure}");
            await harness.VerifyIdentityAndShapesAsync();
        }
        finally { gate.Release(); }
    }

    private sealed class MaintenanceSendGateListener(IServerTransportListener inner) : IServerTransportListener
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Arm() => Volatile.Write(ref _armed, 1);
        internal void Release() => _released.TrySetResult();
        public EndPoint? LocalEndPoint => inner.LocalEndPoint;
        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => new MaintenanceSendGateConnection(await inner.AcceptAsync(cancellationToken), this);
        public ValueTask DisposeAsync() { Release(); return inner.DisposeAsync(); }

        private sealed class MaintenanceSendGateConnection : ITransportConnection, ITransportSecurityHandshake, ITransportSecurityInfo
        {
            private readonly ITransportConnection _inner;
            internal MaintenanceSendGateConnection(ITransportConnection connection, MaintenanceSendGateListener owner)
            {
                _inner = connection;
                Output = new MaintenanceSendGateWriter(connection.Output, owner);
            }
            public string Id => _inner.Id;
            public PipeReader Input => _inner.Input;
            public PipeWriter Output { get; }
            public EndPoint? LocalEndPoint => _inner.LocalEndPoint;
            public EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;
            public SslProtocols Protocol => ((ITransportSecurityInfo)_inner).Protocol;
            public TlsCipherSuite CipherSuite => ((ITransportSecurityInfo)_inner).CipherSuite;
            public ValueTask AuthenticateAsync(CancellationToken cancellationToken)
                => ((ITransportSecurityHandshake)_inner).AuthenticateAsync(cancellationToken);
            public ValueTask DisposeAsync() => _inner.DisposeAsync();
        }

        private sealed class MaintenanceSendGateWriter(PipeWriter inner, MaintenanceSendGateListener owner) : PipeWriter
        {
            public override void Advance(int bytes) => inner.Advance(bytes);
            public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
            public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
            public override void CancelPendingFlush() { owner.Release(); inner.CancelPendingFlush(); }
            public override void Complete(Exception? exception = null) { owner.Release(); inner.Complete(exception); }
            public override ValueTask CompleteAsync(Exception? exception = null) { owner.Release(); return inner.CompleteAsync(exception); }
            public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
                => Volatile.Read(ref owner._armed) == 0 ? inner.FlushAsync(cancellationToken) : HeldFlushAsync(cancellationToken);
            private async ValueTask<FlushResult> HeldFlushAsync(CancellationToken cancellationToken)
            {
                owner.Entered.TrySetResult();
                await owner._released.Task.WaitAsync(cancellationToken);
                return await inner.FlushAsync(cancellationToken);
            }
        }
    }
}
