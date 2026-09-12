using System.Collections.Generic;

namespace SharpLink.UnitTests.Builder;

internal sealed class BuilderFaultInjectionProbe
{
    private readonly List<string> _acquisitions = [];
    private readonly List<string> _cleanups = [];
    private readonly Dictionary<string, int> _cleanupCounts = new(StringComparer.Ordinal);

    internal void RecordAcquisition(string resource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        _acquisitions.Add(resource);
    }

    internal void RecordCleanup(string resource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        _cleanups.Add(resource);
        _cleanupCounts.TryGetValue(resource, out var count);
        _cleanupCounts[resource] = count + 1;
    }

    internal void AssertAcquisitionOrder(params string[] expected)
        => AssertSequence(_acquisitions, expected, "resource acquisition");

    internal void AssertReverseCleanupAndExactlyOnce()
    {
        var expected = new string[_acquisitions.Count];
        for (var index = 0; index < _acquisitions.Count; index++)
            expected[index] = _acquisitions[_acquisitions.Count - index - 1];
        AssertSequence(_cleanups, expected, "resource cleanup");

        for (var index = 0; index < _acquisitions.Count; index++)
        {
            var resource = _acquisitions[index];
            Ensure(_cleanupCounts.TryGetValue(resource, out var count) && count == 1,
                $"resource '{resource}' must be cleaned exactly once");
        }
    }

    internal static void AssertFailureOrder(Exception failure, params string[] expectedMessages)
    {
        var flattened = new List<Exception>();
        Flatten(failure, flattened);
        var searchIndex = 0;
        for (var index = 0; index < expectedMessages.Length; index++)
        {
            var expected = expectedMessages[index];
            while (searchIndex < flattened.Count &&
                   !flattened[searchIndex].Message.Contains(expected, StringComparison.Ordinal))
            {
                searchIndex++;
            }

            Ensure(searchIndex < flattened.Count,
                $"failure chain must retain '{expected}' after prior failures");
            searchIndex++;
        }
    }

    private static void Flatten(Exception exception, List<Exception> destination)
    {
        if (exception is AggregateException aggregate)
        {
            for (var index = 0; index < aggregate.InnerExceptions.Count; index++)
                Flatten(aggregate.InnerExceptions[index], destination);
            return;
        }

        destination.Add(exception);
        if (exception.InnerException is { } inner)
            Flatten(inner, destination);
    }

    private static void AssertSequence(
        IReadOnlyList<string> actual,
        IReadOnlyList<string> expected,
        string operation)
    {
        Ensure(actual.Count == expected.Count,
            $"{operation} count must be {expected.Count}, but was {actual.Count}");
        for (var index = 0; index < expected.Count; index++)
        {
            Ensure(StringComparer.Ordinal.Equals(actual[index], expected[index]),
                $"{operation} index {index} must be '{expected[index]}', but was '{actual[index]}'");
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
