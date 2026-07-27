using System.Collections;
using System.Collections.Generic;

namespace SharpLink.Sdk;

/// <summary>Represents an immutable, ordered collection of RPC metadata entries.</summary>
/// <example>
/// <code>
/// var metadata = new SharpLinkMetadata(
///     new KeyValuePair&lt;string, string&gt;("tenant", "factory-a"));
/// </code>
/// </example>
public sealed class SharpLinkMetadata : IReadOnlyList<KeyValuePair<string, string>>
{
    private readonly KeyValuePair<string, string>[] _entries;

    /// <summary>Creates an immutable metadata snapshot.</summary>
    /// <param name="entries">Ordered metadata entries with non-empty keys and non-null values.</param>
    public SharpLinkMetadata(params KeyValuePair<string, string>[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = new KeyValuePair<string, string>[entries.Length];
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (string.IsNullOrWhiteSpace(entry.Key))
                throw new ArgumentException("Metadata keys cannot be null, empty, or whitespace.", nameof(entries));
            if (entry.Value is null)
                throw new ArgumentException("Metadata values cannot be null.", nameof(entries));
            _entries[index] = entry;
        }
    }

    private SharpLinkMetadata(KeyValuePair<string, string>[] validatedEntries, bool takeOwnership)
        => _entries = validatedEntries;

    internal static SharpLinkMetadata FromValidatedEntries(
        KeyValuePair<string, string>[] validatedEntries)
        => new(validatedEntries, takeOwnership: true);

    /// <summary>Gets the number of metadata entries.</summary>
    public int Count => _entries.Length;

    /// <summary>Gets one metadata entry by insertion order.</summary>
    /// <param name="index">The zero-based entry index.</param>
    public KeyValuePair<string, string> this[int index] => _entries[index];

    /// <summary>Returns an enumerator over the immutable entries.</summary>
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        => ((IEnumerable<KeyValuePair<string, string>>)_entries).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _entries.GetEnumerator();
}
