using System.Linq;

namespace SharpLink.UnitTests.Runtime;

public sealed class SmallStreamRouteTableTests
{
    [Test]
    public void TinyAndPromotedTableMatchDictionaryAfterEveryOperation()
    {
        ushort[] allIds = [1, 2, 7, 128, 65535];
        for (int seed = 0; seed < 10000; seed++)
        {
            var random = new Random(seed); var actual = new SmallStreamRouteTable<object>(); var expected = new Dictionary<ushort, object>();
            int keyCount = seed % 2 == 0 ? 2 : allIds.Length;
            for (int step = 0; step < 80; step++)
            {
                var id = allIds[random.Next(keyCount)]; int op = random.Next(10);
                if (op < 5) { if (!expected.ContainsKey(id)) { var value = new object(); expected.Add(id, value); actual.Add(id, value); } }
                else if (op < 9) { var a = actual.Remove(id, out var av); var b = expected.Remove(id, out var bv); Check(a == b && ReferenceEquals(av, bv), seed, step, "remove"); }
                else { actual.Clear(); expected.Clear(); }
                Check(actual.Count == expected.Count, seed, step, "count");
                foreach (var key in allIds) { var a = actual.TryGetValue(key, out var av); var b = expected.TryGetValue(key, out var bv); Check(a == b && ReferenceEquals(av, bv), seed, step, "lookup " + key); }
                var items = new List<KeyValuePair<ushort, object>>(); foreach (var pair in actual) items.Add(pair);
                Check(items.SequenceEqual(expected), seed, step, "enumeration");
                Check(actual.GetValuesSnapshot().SequenceEqual(expected.Values), seed, step, "snapshot");
            }
        }
    }
    static void Check(bool okay, int seed, int step, string operation) { if (!okay) throw new Exception($"seed {seed} step {step}: {operation}"); }
}
