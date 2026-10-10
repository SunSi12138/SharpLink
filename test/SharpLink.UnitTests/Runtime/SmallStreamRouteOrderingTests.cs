using System.Linq;

namespace SharpLink.UnitTests.Runtime;

public sealed class SmallStreamRouteOrderingTests
{
    [Test]
    public void ChildTeardownMatchesDictionaryEntrySlotOrder()
    {
        ushort[] ids = [1, 2, 3, 7, 127, 128, 65535];
        for (int seed = 0; seed < 10000; seed++)
        {
            var random = new Random(seed); var trace = new List<ushort>(); var operations = new List<string>();
            var manager = new StreamManager(); manager.Register(17, 0, new Recorder(0, trace));
            var oracle = new Dictionary<ushort, bool>();
            for (int step = 0; step < 30; step++)
            {
                var id = ids[random.Next(ids.Length)];
                if (random.Next(3) == 0) { operations.Add("remove " + id); manager.Unregister(17, id); oracle.Remove(id); }
                else if (!oracle.ContainsKey(id)) { operations.Add("add " + id); manager.Register(17, id, new Recorder(id, trace)); oracle.Add(id, true); }
            }
            trace.Clear(); if (seed % 2 == 0) manager.CompleteAll(null); else manager.CompleteRequestStreams(17, null);
            var expected = new ushort[] { 0 }.Concat(oracle.Keys).ToArray();
            if (!trace.SequenceEqual(expected)) throw new Exception($"seed {seed}: expected {string.Join(',', expected)}, got {string.Join(',', trace)}; {string.Join(';', operations)}");
            if (manager.ActiveStreamCount != 0) throw new Exception("nonzero active count");
        }
    }
    sealed class Recorder(ushort id, List<ushort> trace) : IStreamDispatcher
    {
        public ValueTask DispatchAsync(ReadOnlySequence<byte> p) => ValueTask.CompletedTask;
        public void Complete(bool e, string? s) => Complete(null); public void Complete(Exception? e) => trace.Add(id);
    }
}
