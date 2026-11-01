using System.Buffers.Binary;
using System.IO.Pipelines;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public partial class ServerBindingTableTests
{
    [Test]
    public async Task LargeTableWithContinuouslyRaisedRetirementsStillParsesProgressEveryReadTurn()
    {
        var input = new Pipe();
        var slices = 0;
        var reader = new FairnessProgressReader(input.Reader, input.Writer, () => Volatile.Read(ref slices));
        var connection = IntakeConnection(reader, new Pipe().Writer);
        var slots = new ServerBindingSlotQuota(600);
        var table = connection.InitializeBindings(slots);
        var controls = new ServerBindingRegistration[600];
        var payloads = new ServiceRegistration[600];
        for (var index = 0; index < controls.Length; index++)
        {
            (controls[index], payloads[index]) = RegistrationWithPayload(index + 1);
            Bind(table, (ulong)index + 1, controls[index]);
            if ((index & 1) == 0)
            { controls[index].Retire(); payloads[index].DeclaredFacts!.Dispose(); }
        }
        var nextRetirement = 1;
        var keepRetiring = 1;
        connection.BindingMaintenanceSliceCompletedForTesting = () =>
        {
            Interlocked.Increment(ref slices);
            if (Volatile.Read(ref keepRetiring) == 0) return;
            if (nextRetirement < controls.Length)
            {
                controls[nextRetirement].Retire();
                payloads[nextRetirement].DeclaredFacts!.Dispose();
                nextRetirement += 2;
            }
            connection.RequestBindingMaintenance();
        };
        reader.BeforeFirstRead = () => Ensure(slots.Used >= 600 - 256);
        connection.RequestBindingMaintenance();
        await using var server = IntakeServer();
        var loop = RunIntake(server, connection);
        try
        {
            await reader.TwelveFramesConsumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(reader.SlicesAtGoal == 12 && reader.ReadsAtGoal == 12);
            Ensure(reader.FramesConsumed >= 12 && connection.Session.IsConnected);
        }
        finally
        {
            Volatile.Write(ref keepRetiring, 0);
            reader.StopProducing();
            await input.Writer.CompleteAsync();
            await loop.WaitAsync(TimeSpan.FromSeconds(5));
            await connection.CloseAsync();
            foreach (var payload in payloads) await payload.DisposeAsync();
        }
        Ensure(slots.Used == 0);
    }

    [Test]
    public async Task Last256SliceDirtyResetStartsANewPassOnlyAfterTheInterveningRead()
    {
        var input = new Pipe();
        var reader = new FairnessCountingReader(input.Reader);
        var connection = IntakeConnection(reader, new Pipe().Writer);
        var slots = new ServerBindingSlotQuota(600);
        var table = connection.InitializeBindings(slots);
        var (late, latePayload) = RegistrationWithPayload(1);
        var (early, earlyPayload) = RegistrationWithPayload(2);
        Bind(table, 1, late); // Slot zero has already been scanned when final256 retires it.
        for (ulong id = 2; id <= 600; id++) Bind(table, id, early);
        early.Retire(); await earlyPayload.DisposeAsync();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var passes = 0;
        var slices = 0;
        var firstPassReadCount = -1;
        var secondPassReadCount = -1;
        connection.BindingMaintenanceSliceCompletedForTesting = () => Interlocked.Increment(ref slices);
        connection.BindingMaintenanceScanCompletedForTesting = () =>
        {
            if (++passes == 1)
            {
                firstPassReadCount = reader.Reads;
                late.Retire(); latePayload.DeclaredFacts!.Dispose();
                connection.RequestBindingMaintenance();
            }
            else
            {
                secondPassReadCount = reader.Reads;
                completed.TrySetResult();
            }
        };
        connection.RequestBindingMaintenance();
        await using var server = IntakeServer();
        var loop = RunIntake(server, connection);
        try
        {
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var turnsPerPass = (table.SlotCount + 255) / 256;
            Ensure(firstPassReadCount == turnsPerPass - 1 && secondPassReadCount == 2 * turnsPerPass - 1);
            Ensure(Volatile.Read(ref slices) == 2 * turnsPerPass && passes == 2 && slots.Used == 0);
            Ensure(connection.Session.IsConnected); // No network bytes needed to finish the dirty second pass.
        }
        finally
        {
            await input.Writer.CompleteAsync();
            await loop.WaitAsync(TimeSpan.FromSeconds(5));
            await latePayload.DisposeAsync();
            await connection.CloseAsync();
        }
    }

    private sealed class FairnessProgressReader(PipeReader inner, PipeWriter producer, Func<int> slices) : PipeReader
    {
        private ReadOnlySequence<byte> _buffer;
        private int _stopped;
        private int _reads;
        internal Action? BeforeFirstRead;
        internal int FramesConsumed;
        internal int SlicesAtGoal;
        internal int ReadsAtGoal;
        internal readonly TaskCompletionSource TwelveFramesConsumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void StopProducing() => Volatile.Write(ref _stopped, 1);
        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (++_reads == 1) BeforeFirstRead?.Invoke();
            if (_reads <= 12 && Volatile.Read(ref _stopped) == 0)
            {
                var pong = new byte[ProtocolV2Constants.HeaderBytes + 8];
                pong[0] = ProtocolV2Constants.Magic;
                BinaryPrimitives.WriteInt32LittleEndian(pong.AsSpan(1), 8);
                pong[5] = (byte)ProtocolV2FrameType.Pong;
                await producer.WriteAsync(pong, cancellationToken);
            }
            var result = await inner.ReadAsync(cancellationToken);
            _buffer = result.Buffer;
            return result;
        }
        public override bool TryRead(out ReadResult result) => throw new NotSupportedException();
        public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            var bytes = _buffer.Slice(0, consumed).Length;
            if (bytes != 0)
            {
                Ensure(bytes == ProtocolV2Constants.HeaderBytes + 8 && slices() <= _reads);
                FramesConsumed++;
            }
            inner.AdvanceTo(consumed, examined);
            _buffer = default;
            if (FramesConsumed == 12 && !TwelveFramesConsumed.Task.IsCompleted)
            {
                SlicesAtGoal = slices(); ReadsAtGoal = _reads;
                TwelveFramesConsumed.TrySetResult();
            }
        }
        public override void CancelPendingRead() => inner.CancelPendingRead();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
    }

    private sealed class FairnessCountingReader(PipeReader inner) : PipeReader
    {
        internal int Reads;
        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Reads); return inner.ReadAsync(cancellationToken); }
        public override bool TryRead(out ReadResult result) => inner.TryRead(out result);
        public override void AdvanceTo(SequencePosition consumed) => inner.AdvanceTo(consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => inner.AdvanceTo(consumed, examined);
        public override void CancelPendingRead() => inner.CancelPendingRead();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
    }
}
