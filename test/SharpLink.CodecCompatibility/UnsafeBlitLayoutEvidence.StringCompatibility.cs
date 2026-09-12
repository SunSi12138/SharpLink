using System;

namespace SharpLink.CodecCompatibility;

internal static class UnsafeBlitLayoutEvidenceStringCompatibility
{
    internal static bool Contains(this string value, char character, StringComparison comparison)
        => value.IndexOf(character) >= 0;
}
