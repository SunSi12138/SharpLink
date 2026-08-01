using System;
using System.Collections.Concurrent;
using System.Linq;
using SharpLink.Abstractions;

namespace SharpLink.LoadTestBase;

public sealed class FailureRecorder
{
    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _firstDetails = new(StringComparer.Ordinal);

    public void Record(Exception ex)
    {
        var key = ex switch
        {
            SharpLinkException sharpLink => $"{nameof(SharpLinkException)}[{sharpLink.Code}]",
            ArgumentOutOfRangeException outOfRange =>
                $"{nameof(ArgumentOutOfRangeException)}[{outOfRange.ParamName}={outOfRange.ActualValue}]",
            ArgumentException argument when !string.IsNullOrEmpty(argument.ParamName) =>
                $"{argument.GetType().Name}[{argument.ParamName}]",
            InvalidOperationException invalidOperation =>
                $"{nameof(InvalidOperationException)}[{invalidOperation.Message}]",
            _ => ex.GetType().Name
        };
        _counts.AddOrUpdate(key, 1, static (_, old) => old + 1);
        if (_firstDetails.TryAdd(key, ex.ToString()))
            Console.Error.WriteLine($"[FailureDetail:{key}] {ex}");
    }

    public string Top(int count)
    {
        if (_counts.IsEmpty)
            return string.Empty;

        return string.Join(", ", _counts.OrderByDescending(x => x.Value).Take(count).Select(x => $"{x.Key}:{x.Value}"));
    }
}
