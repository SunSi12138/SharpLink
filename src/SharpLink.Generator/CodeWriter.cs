namespace SharpLink.Generator;

internal sealed class CodeWriter
{
    private const string Indentation = "    ";

    private readonly StringBuilder _builder;
    private int _indentLevel;

    internal CodeWriter(StringBuilder builder)
        => _builder = builder ?? throw new ArgumentNullException(nameof(builder));

    internal void WriteLine(string? line = null)
    {
        if (line is null)
        {
            _builder.AppendLine();
            return;
        }

        for (var index = 0; index < _indentLevel; index++)
            _builder.Append(Indentation);

        _builder.AppendLine(line);
    }

    internal void OpenBlock(string header)
    {
        WriteLine(header);
        WriteLine("{");
        _indentLevel++;
    }

    internal void CloseBlock()
    {
        if (_indentLevel == 0)
            throw new InvalidOperationException("Cannot close a code block when no block is open.");

        _indentLevel--;
        WriteLine("}");
    }
}
