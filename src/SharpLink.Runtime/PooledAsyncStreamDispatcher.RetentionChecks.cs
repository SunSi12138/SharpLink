namespace SharpLink.Runtime;

// Test-boundary inspection stays separate from the streaming and pooling hot paths.
internal sealed partial class PooledAsyncStreamDispatcher<T>
{
    internal bool HasRetainedReferencesForTests
    {
        get
        {
            if (_codec is not null || _error is not null || Volatile.Read(ref _dispatchState) is not null ||
                _bytesConsumed is not null || _localAbortBytesConsumed is not null ||
                _resolvedBytesConsumed is not null || _receiveCreditLease.Owner is not null ||
                _receiveCreditLease.State is not null || _localAbortResolvedBytesConsumed is not null ||
                _localAbortReceiveCreditLease.Owner is not null || _localAbortReceiveCreditLease.State is not null ||
                _consumerAbandoned is not null || _consumerAbandonedAsync is not null ||
                _current is not null || _enumerationToken.CanBeCanceled ||
                _additionalEnumerationToken.CanBeCanceled ||
                !_enumerationCancellationRegistration.Equals(default) ||
                !_additionalEnumerationCancellationRegistration.Equals(default) ||
                Volatile.Read(ref _disposeCompletion) is not null ||
                Volatile.Read(ref _remoteTerminalPublication) is not null ||
                Volatile.Read(ref _beforeConcurrentDisposeCompletionInstallForTests) is not null ||
                Volatile.Read(ref _beforeRemoteTerminalPublicationPublishForTests) is not null ||
                Volatile.Read(ref _beforeRemoteTerminalPublicationCompletionInstallForTests) is not null ||
                Volatile.Read(ref _afterRemoteTerminalPublicationCompletionInstallForTests) is not null ||
                Volatile.Read(ref _beforeProducerOperationAcquireForTests) is not null ||
                Volatile.Read(ref _beforeConsumerWaitOwnerAcquireForTests) is not null ||
                Volatile.Read(ref _afterConsumerWaitResultForTests) is not null ||
                Volatile.Read(ref _beforeReturnTransitionForTests) is not null ||
                Volatile.Read(ref _afterReturnTransitionForTests) is not null)
            {
                return true;
            }

            if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                return false;
            if (SegmentHasReferences(_firstSegment))
                return true;
            foreach (var segment in _freeSegments)
            {
                if (SegmentHasReferences(segment))
                    return true;
            }
            return false;
        }
    }

    private static bool SegmentHasReferences(BufferSegment segment)
    {
        for (var index = 0; index < segment.Items.Length; index++)
        {
            if (segment.Items[index] is not null)
                return true;
        }
        return false;
    }
}
