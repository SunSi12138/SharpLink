using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SharpLink.Abstractions;

/// <summary>
/// Isolates application-owned diagnostics callbacks from RPC lifecycle state. The normal path is
/// allocation-free: overloads are explicit so tags are passed as structs rather than params arrays.
/// </summary>
internal static class SharpLinkTelemetryObserverIsolation
{
    internal static Activity? StartActivity(ActivitySource source, string name, ActivityKind kind)
    {
        var previous = Activity.Current;
        try
        {
            return source.StartActivity(name, kind);
        }
        catch (Exception)
        {
            // ActivityStarted callbacks run after the activity becomes current. Restore the
            // caller's ambient context when an application listener aborts that notification.
            Activity.Current = previous;
            return null;
        }
    }

    internal static void DisposeActivity(Activity? activity)
    {
        if (activity is null)
            return;
        try
        {
            activity.Dispose();
        }
        catch (Exception)
        {
            // ActivityStopped normally restores Current before notifying listeners. Keep the
            // ambient context sane even if a listener throws earlier than expected.
            if (ReferenceEquals(Activity.Current, activity))
                Activity.Current = activity.Parent;
        }
    }

    internal static void Add(Counter<long> instrument, long value)
    {
        if (!instrument.Enabled)
            return;
        try
        {
            instrument.Add(value);
        }
        catch (Exception)
        {
        }
    }

    internal static void Add(
        Counter<long> instrument,
        long value,
        KeyValuePair<string, object?> tag)
    {
        if (!instrument.Enabled)
            return;
        try
        {
            instrument.Add(value, tag);
        }
        catch (Exception)
        {
        }
    }

    internal static void Add(
        Counter<long> instrument,
        long value,
        KeyValuePair<string, object?> tag1,
        KeyValuePair<string, object?> tag2)
    {
        if (!instrument.Enabled)
            return;
        try
        {
            instrument.Add(value, tag1, tag2);
        }
        catch (Exception)
        {
        }
    }

    internal static void Add(
        Counter<long> instrument,
        long value,
        KeyValuePair<string, object?> tag1,
        KeyValuePair<string, object?> tag2,
        KeyValuePair<string, object?> tag3)
    {
        if (!instrument.Enabled)
            return;
        try
        {
            instrument.Add(value, tag1, tag2, tag3);
        }
        catch (Exception)
        {
        }
    }

    internal static void Add(Counter<long> instrument, long value, in TagList tags)
    {
        if (!instrument.Enabled)
            return;
        try
        {
            instrument.Add(value, tags);
        }
        catch (Exception)
        {
        }
    }

    internal static void Add(UpDownCounter<long> instrument, long value)
    {
        if (!instrument.Enabled)
            return;
        try
        {
            instrument.Add(value);
        }
        catch (Exception)
        {
        }
    }

    internal static void Add(
        UpDownCounter<long> instrument,
        long value,
        KeyValuePair<string, object?> tag)
    {
        if (!instrument.Enabled)
            return;
        try
        {
            instrument.Add(value, tag);
        }
        catch (Exception)
        {
        }
    }

    internal static void Add(
        UpDownCounter<long> instrument,
        long value,
        KeyValuePair<string, object?> tag1,
        KeyValuePair<string, object?> tag2,
        KeyValuePair<string, object?> tag3)
    {
        if (!instrument.Enabled)
            return;
        try
        {
            instrument.Add(value, tag1, tag2, tag3);
        }
        catch (Exception)
        {
        }
    }

    internal static void Record(Histogram<double> instrument, double value)
    {
        if (!instrument.Enabled)
            return;
        try
        {
            instrument.Record(value);
        }
        catch (Exception)
        {
        }
    }

    internal static void Record(Histogram<double> instrument, double value, in TagList tags)
    {
        if (!instrument.Enabled)
            return;
        try
        {
            instrument.Record(value, tags);
        }
        catch (Exception)
        {
        }
    }
}
