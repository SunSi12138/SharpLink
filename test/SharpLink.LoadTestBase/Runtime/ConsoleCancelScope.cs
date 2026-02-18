using System;
using System.Threading;

namespace SharpLink.LoadTestBase;

public sealed class ConsoleCancelScope : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly ConsoleCancelEventHandler _handler;
    private bool _disposed;

    public ConsoleCancelScope()
    {
        _handler = OnCancelKeyPress;
        Console.CancelKeyPress += _handler;
    }

    public CancellationToken Token => _cts.Token;

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        _cts.Cancel();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Console.CancelKeyPress -= _handler;
        _cts.Dispose();
    }
}

