using System;
using System.Collections.Concurrent;
using System.Linq;

namespace SharpLink.LoadTestBase;

public sealed class FailureRecorder
{
    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);

    public void Record(Exception ex)
    {
        var key = ex.GetType().Name;
        _counts.AddOrUpdate(key, 1, static (_, old) => old + 1);
    }

    public string Top(int count)
    {
        if (_counts.IsEmpty)
            return string.Empty;

        return string.Join(", ", _counts.OrderByDescending(x => x.Value).Take(count).Select(x => $"{x.Key}:{x.Value}"));
    }
}

