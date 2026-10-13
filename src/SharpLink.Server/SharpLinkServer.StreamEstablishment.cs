namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private ValueTask InvokeAdmittedServiceAsync(
        ServiceRegistration registration,
        ServerConnectionState connection,
        RpcSession session,
        long methodId,
        long requestId,
        ReadOnlySequence<byte> arguments,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkCallContextSnapshot context)
    {
        var descriptor = GetMethodDescriptor(registration.Stub, methodId);
        if (descriptor.Kind != RpcMethodKind.OneWay)
        {
            ReservePreInvocationRequestStreams(
                session, descriptor.ClientStreamCount, requestId, cancellationToken);
        }
        if (descriptor.Kind is RpcMethodKind.ClientStreaming or RpcMethodKind.DuplexStreaming)
        {
            var acceptance = session.SendRequestAcceptedAsync(requestId, cancellationToken);
            if (!acceptance.IsCompletedSuccessfully)
            {
                // Generated stubs normally decode synchronously before dispatch returns to the
                // PipeReader. A backpressured acceptance frame introduces an earlier await, so
                // this branch must retain its own bounded payload owner before releasing the pipe.
                var owner = _runtimeContext.Buffers.Rent(checked((int)arguments.Length));
                try
                {
                    foreach (var segment in arguments)
                        owner.Write(segment.Span);
                }
                catch
                {
                    _runtimeContext.Buffers.Return(owner);
                    throw;
                }
                return InvokeAfterStreamAcceptanceAsync(acceptance, owner, registration, connection,
                    session, methodId, requestId, output, cancellationToken, context, descriptor);
            }
        }
        return InvokeRegisteredServiceAsync(registration, connection, session, methodId, requestId,
            arguments, output, cancellationToken, context, moduleLeaseRetained: true, descriptor);
    }

    private async ValueTask InvokeAfterStreamAcceptanceAsync(
        ValueTask acceptance,
        IRpcByteBufferWriter argumentOwner,
        ServiceRegistration registration,
        ServerConnectionState connection,
        RpcSession session,
        long methodId,
        long requestId,
        IRpcByteBufferWriter? output,
        CancellationToken cancellationToken,
        SharpLinkCallContextSnapshot context,
        RpcMethodDescriptor descriptor)
    {
        try
        {
            await acceptance.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await InvokeRegisteredServiceAsync(registration, connection, session, methodId, requestId,
                new ReadOnlySequence<byte>(argumentOwner.WrittenMemory), output, cancellationToken,
                context, moduleLeaseRetained: true, descriptor).ConfigureAwait(false);
        }
        finally
        {
            _runtimeContext.Buffers.Return(argumentOwner);
        }
    }
}
