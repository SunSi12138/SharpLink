namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private sealed partial class DtoAnalysisState
    {
        internal ImmutableArray<GeneratedCodecModel> FilterFailedCodecClosure(
            ImmutableArray<GeneratedCodecModel> codecs)
        {
            bool changed;
            do
            {
                changed = false;
                foreach (var codec in codecs)
                {
                    if (_failed.Contains(codec.TypeName))
                        continue;
                    if (GetCodecDependencies(codec).Any(_failed.Contains))
                        changed |= _failed.Add(codec.TypeName);
                }
            }
            while (changed);

            return codecs
                .Where(codec => !_failed.Contains(codec.TypeName))
                .ToImmutableArray();
        }
    }
}
