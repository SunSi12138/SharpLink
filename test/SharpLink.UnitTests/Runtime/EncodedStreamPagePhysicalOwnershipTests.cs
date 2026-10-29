using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpLink.UnitTests.Runtime;

public class EncodedStreamPagePhysicalOwnershipTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DependencyCycleMustRetainAllPagesUntilEveryPhysicalMemberFinishes(bool throwOnReturn)
    {
        var returned = 0;
        var firstBudget = new EncodedStreamPageBudget(4096, static size => new byte[size], _ =>
        {
            Interlocked.Increment(ref returned);
            if (throwOnReturn)
                throw new IOException("test pool return failure");
        });
        var secondBudget = new EncodedStreamPageBudget(4096, static size => new byte[size], _ => Interlocked.Increment(ref returned));
        var firstWriter = new EncodedStreamPageWriter(firstBudget);
        var secondWriter = new EncodedStreamPageWriter(secondBudget);
        Ensure(firstWriter.TryBeginFrame(1) && secondWriter.TryBeginFrame(1), "both producers must own real pages");
        firstWriter.GetSpan(1)[0] = 17;
        secondWriter.GetSpan(1)[0] = 29;
        using var firstLifetime = new CancellationTokenSource();
        using var secondLifetime = new CancellationTokenSource();
        var firstTerminal = new OperationCanceledException("first selected terminal");
        var secondTerminal = new OperationCanceledException("second selected terminal");
        var firstOwner = SharpLinkPhysicalWork.CreateStreamOwner(firstTerminal, firstLifetime, firstWriter);
        var secondOwner = SharpLinkPhysicalWork.CreateStreamOwner(secondTerminal, secondLifetime, secondWriter);
        var firstInternalWaitEnded = Signal();
        var secondInternalWaitEnded = Signal();
        var finishFirst = Signal();
        var finishSecond = Signal();
        var firstCleanup = Cleanup(firstOwner, secondTerminal, firstInternalWaitEnded, finishFirst);
        var secondCleanup = Cleanup(secondOwner, firstTerminal, secondInternalWaitEnded, finishSecond);
        firstOwner.Track(firstCleanup);
        secondOwner.Track(secondCleanup);
        try
        {
            await Task.WhenAll(firstInternalWaitEnded.Task, secondInternalWaitEnded.Task).WaitAsync(TimeSpan.FromSeconds(10));
            finishFirst.TrySetResult();
            await firstCleanup.WaitAsync(TimeSpan.FromSeconds(10));
            Ensure(!firstOwner.Completion.IsCompleted && !secondOwner.Completion.IsCompleted,
                "SCC internal metadata wait release must not complete either physical owner");
            Ensure(firstBudget.BytesOwned == 4096 && secondBudget.BytesOwned == 4096 && returned == 0,
                "even the locally finished SCC member must retain its producer page");
            Ensure(firstWriter.GetSpan(1)[0] == 17 && secondWriter.GetSpan(1)[0] == 29,
                "both pages must remain valid until all real cleanup has finished");
        }
        finally
        {
            finishFirst.TrySetResult();
            finishSecond.TrySetResult();
            await Task.WhenAll(firstOwner.Completion, secondOwner.Completion).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Ensure(firstBudget.BytesOwned == 0 && secondBudget.BytesOwned == 0 && returned == 2,
            "a pool cleanup fault must not strand the other page, budgets or owner completions");
        EnsureLifetimeDisposed(firstLifetime);
        EnsureLifetimeDisposed(secondLifetime);
        var resourceField = typeof(SharpLinkPhysicalWork.PhysicalOwner).GetField("_retainedPageWriter",
            BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new Exception("retained page field missing");
        Ensure(resourceField.GetValue(firstOwner) is null && resourceField.GetValue(secondOwner) is null,
            "retained terminal associations must not root released page writers");
        Ensure(ReferenceEquals(SharpLinkPhysicalWork.GetAbandonedUserWork(firstTerminal), firstOwner.Completion),
            "cleanup failures must not replace the selected terminal association");
    }

    [Test]
    public void UnassociatedTransferMustLeaveProducerOwnershipLocal()
    {
        var returned = 0;
        var budget = new EncodedStreamPageBudget(4096, static size => new byte[size], _ => returned++);
        var writer = new EncodedStreamPageWriter(budget);
        Ensure(writer.TryBeginFrame(1), "producer must rent a page");
        Ensure(SharpLinkPhysicalWork.TransferStreamLifetime(new Exception("application failure"), null, writer) is null,
            "unassociated user failures must not create a physical transfer");
        Ensure(budget.BytesOwned == 4096 && returned == 0, "a rejected transfer leaves the producer lease local");
        writer.Dispose();
        Ensure(budget.BytesOwned == 0 && returned == 1, "the local scope still releases its page exactly once");
    }

    [Test]
    public async Task AssociatedTransferMustRetainProducerUntilItsDescendantFinishes()
    {
        var budget = new EncodedStreamPageBudget(4096, static size => new byte[size], static _ => { });
        var writer = new EncodedStreamPageWriter(budget);
        Ensure(writer.TryBeginFrame(1), "producer must own a page before transfer");
        var descendantTerminal = new Exception("descendant terminal");
        var descendant = SharpLinkPhysicalWork.CreateOwner(descendantTerminal);
        var release = Signal();
        descendant.Track(release.Task);
        var terminal = SharpLinkPhysicalWork.TransferStreamLifetime(descendantTerminal, null, writer)
            ?? throw new Exception("associated transfer must create a carrier");
        var work = SharpLinkPhysicalWork.GetAbandonedUserWork(terminal)
            ?? throw new Exception("transferred page must have physical cleanup");
        try
        {
            Ensure(budget.BytesOwned == 4096 && !work.IsCompleted,
                "successful transfer must retain P beyond the already-selected terminal");
        }
        finally
        {
            release.TrySetResult();
            await work.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Ensure(budget.BytesOwned == 0, "only the physical owner releases a successfully transferred producer page");
    }

    [Test]
    public async Task RetainedTerminalMustNotRootAReleasedPageOrProducer()
    {
        var result = CreateReleasedProducerReferences();
        await result.Cleanup.WaitAsync(TimeSpan.FromSeconds(10));
        for (var attempt = 0; attempt < 3 && (result.Writer.IsAlive || result.Buffer.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Ensure(!result.Writer.IsAlive && !result.Buffer.IsAlive,
            "keeping a terminal association must not keep the released page writer or buffer alive");
        GC.KeepAlive(result.Terminal);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Exception Terminal, Task Cleanup, WeakReference Writer, WeakReference Buffer) CreateReleasedProducerReferences()
    {
        var budget = new EncodedStreamPageBudget(4096, static size => new byte[size], static _ => { });
        var writer = new EncodedStreamPageWriter(budget);
        Ensure(writer.TryBeginFrame(1), "producer must acquire a real array");
        Ensure(MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)writer.GetMemory(1), out var array),
            "page memory must be array backed");
        var terminal = new Exception("retained terminal");
        var owner = SharpLinkPhysicalWork.CreateStreamOwner(terminal, null, writer);
        var references = (terminal, owner.Completion, new WeakReference(writer), new WeakReference(array.Array!));
        owner.Track(Task.CompletedTask);
        return references;
    }

    private static async Task Cleanup(SharpLinkPhysicalWork.PhysicalOwner owner, Exception descendant,
        TaskCompletionSource internalWaitEnded, TaskCompletionSource finish)
    {
        await owner.WaitForDescendantAsync(descendant);
        internalWaitEnded.TrySetResult();
        await finish.Task;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void EnsureLifetimeDisposed(CancellationTokenSource source)
    {
        try { _ = source.Token; }
        catch (ObjectDisposedException) { return; }
        throw new Exception("the physical completion group must dispose every linked lifetime");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
