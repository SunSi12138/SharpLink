using System.Threading.Tasks.Sources;

namespace SharpLink.FlowStatePhaseB;

// Typed refill and update messages hold their own completion lifetimes. These
// are preallocated per stream; cold control operations deliberately remain Tasks.
internal sealed partial class GrantAuthority
{
    internal sealed class AcquireCommand : ReusableOwnerCommand<Receipt>, IValueTaskSource<Publication>
    {
        private readonly GrantAuthority _owner;
        internal Lease Lease;
        internal int Bytes;
        internal CancellationToken Token;
        internal bool WriterOwned;
        internal readonly LinkedListNode<AcquireCommand> Node;

        internal AcquireCommand(GrantAuthority owner)
        {
            _owner = owner;
            Node = new LinkedListNode<AcquireCommand>(this);
            Interlocked.Increment(ref owner.ReusableCommandAllocations);
        }

        internal ValueTask<Receipt> Prepare(Lease lease, int bytes, CancellationToken token)
        {
            var result = Begin(token, static state => ((GrantAuthority)state!).WakeCanceledWaiters(), _owner);
            Lease = lease;
            Bytes = bytes;
            Token = token;
            WriterOwned = false;
            return result;
        }

        internal ValueTask<Publication> PreparePublication(Lease lease, int bytes, CancellationToken token)
        {
            var version = BeginToken(token, static state => ((GrantAuthority)state!).WakeCanceledWaiters(), _owner);
            Lease = lease;
            Bytes = bytes;
            Token = token;
            WriterOwned = true;
            return new ValueTask<Publication>(this, version);
        }

        Publication IValueTaskSource<Publication>.GetResult(short token)
            => new(GetResult(token)); // ownership was bound at Reserve, before completion

        public override void Execute() => _owner.AdmitOrQueue(this);

        protected override void OnConsumed()
        {
            lock (Lease.State.Gate)
            {
                // An old result must never clear a replacement generation's pending bit.
                if (Lease.State.Generation == Lease.Generation && ReferenceEquals(Lease.State.AcquireCommand, this))
                    Lease.State.AcquirePending = false;
                base.OnConsumed(); // release slot and admission ownership under the same stream gate
            }
        }
    }

    internal sealed class UpdateCommand : ReusableOwnerCommand<bool>
    {
        private readonly GrantAuthority _owner;
        private Lease _lease;
        private int _bytes;

        internal UpdateCommand(GrantAuthority owner)
        {
            _owner = owner;
            Interlocked.Increment(ref owner.ReusableCommandAllocations);
        }

        internal ValueTask<bool> Prepare(Lease lease, int bytes)
        {
            var result = Begin();
            _lease = lease;
            _bytes = bytes;
            return result;
        }

        public override void Execute()
        {
            bool result;
            try { result = _owner.ApplyWindowUpdate(_lease, _bytes); }
            catch (Exception error) { Fail(error); return; }
            Complete(result);
        }
    }
}
