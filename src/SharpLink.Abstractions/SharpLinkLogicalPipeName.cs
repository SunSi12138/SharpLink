namespace SharpLink.Abstractions;

internal static class SharpLinkLogicalPipeName
{
    internal static void Validate(string name, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, parameterName);
        if (name.Contains('\0') || name.Contains('/') || name.Contains('\\'))
        {
            throw new ArgumentException(
                "A logical pipe name cannot contain NUL or path separators.",
                parameterName);
        }
    }
}
