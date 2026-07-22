namespace SharpLink.Abstractions;

/// <summary>Identifies one case-sensitive logical cluster in a multi-cluster client.</summary>
public readonly record struct SharpLinkClusterKey
{
    /// <summary>Creates a validated cluster key.</summary>
    /// <param name="value">A one to 64 character ASCII cluster key.</param>
    public SharpLinkClusterKey(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                "Cluster keys must contain 1 to 64 ASCII characters, start with a letter or digit, and then contain only letters, digits, '.', '_', or '-'.",
                nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the original, case-sensitive key value.</summary>
    public string Value { get; }

    /// <summary>Returns whether a value satisfies the cluster-key grammar without normalization.</summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64 || !IsAlphaNumeric(value[0]))
            return false;

        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (!IsAlphaNumeric(character) && character is not '.' and not '_' and not '-')
                return false;
        }

        return true;
    }

    /// <summary>Returns the original key value.</summary>
    public override string ToString() => Value ?? string.Empty;

    /// <summary>Creates a validated cluster key from a string.</summary>
    public static implicit operator SharpLinkClusterKey(string value) => new(value);

    private static bool IsAlphaNumeric(char value)
        => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}
