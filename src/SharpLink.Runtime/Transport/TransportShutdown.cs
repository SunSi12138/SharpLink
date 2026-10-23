namespace SharpLink.Runtime;

/// <summary>Cold session shutdown using the transport's existing single-flight disposal owner.</summary>
internal interface IQuiescingTransportConnection
{
    ValueTask DisposeAfterWritesAsync(Task writesDrained, Exception terminalException);
}

internal static class TransportShutdown
{
    internal static async Task DisposeAfterWritesAsync(
        PipeReader input, PipeWriter output, Stream outputStream, Stream? inputStream, Task writesDrained, Exception terminalException)
    {
        // Publish the transport's existing disposal task before invoking any stream/user code.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        Exception? failure = null;
        try { await outputStream.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) when (StreamTransportConnection.IsExpectedDisposeException(exception)) { }
        catch (Exception exception) { failure = exception; }
        if (inputStream is not null)
        {
            try { await inputStream.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) when (StreamTransportConnection.IsExpectedDisposeException(exception)) { }
            catch (Exception exception) { failure = StreamTransportConnection.CombineCleanupExceptions(failure, exception); }
        }

        // Closing the underlying stream requests an abort, not a certificate that its pending
        // write finished. Only the actual pump/IO lifetime can release the pipeline's buffers.
        try { await writesDrained.ConfigureAwait(false); }
        catch (Exception exception) { failure = StreamTransportConnection.CombineCleanupExceptions(failure, exception); }
        // A non-null terminal discards unflushed bytes after the join. Retrying a write on the
        // aborted stream can fail before StreamPipeWriter returns its pooled segments.
        try { await StreamTransportConnection.CompleteWriterAsync(output, terminalException).ConfigureAwait(false); }
        catch (Exception exception) { failure = StreamTransportConnection.CombineCleanupExceptions(failure, exception); }
        try { await StreamTransportConnection.CompleteReaderAsync(input).ConfigureAwait(false); }
        catch (Exception exception) { failure = StreamTransportConnection.CombineCleanupExceptions(failure, exception); }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
