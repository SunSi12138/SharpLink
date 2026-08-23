using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class ServerCallTerminationMapperTests
{
    [Test]
    [Arguments((int)ProtocolV2CancelReason.Unspecified, (int)ServerCallCancellationReason.RemoteCancel)]
    [Arguments((int)ProtocolV2CancelReason.UserCancellation, (int)ServerCallCancellationReason.RemoteCancel)]
    [Arguments((int)ProtocolV2CancelReason.DeadlineExceeded, (int)ServerCallCancellationReason.DeadlineExceeded)]
    [Arguments((int)ProtocolV2CancelReason.ConsumerAbandoned, (int)ServerCallCancellationReason.ConsumerAbandoned)]
    public async Task MapRemoteCancellationReasonShouldPreserveEveryKnownReason(
        int remoteReasonValue,
        int expectedServerReasonValue)
    {
        var actual = ServerCallTerminationMapper.MapRemoteCancellationReason(
            (ProtocolV2CancelReason)remoteReasonValue);

        await Assert.That(actual).IsEqualTo((ServerCallCancellationReason)expectedServerReasonValue);
    }

    [Test]
    public async Task MapRemoteCancellationReasonShouldRejectUnknownReason()
    {
        const ProtocolV2CancelReason unknownReason = (ProtocolV2CancelReason)byte.MaxValue;

        var exception = CaptureException<ArgumentOutOfRangeException>(() =>
            ServerCallTerminationMapper.MapRemoteCancellationReason(unknownReason));

        await Assert.That(exception.ParamName).IsEqualTo("reason");
        await Assert.That(exception.ActualValue).IsEqualTo(unknownReason);
    }

    [Test]
    [Arguments((int)ServerCallCancellationReason.None, "unknown")]
    [Arguments((int)ServerCallCancellationReason.RemoteCancel, "remote_cancel")]
    [Arguments((int)ServerCallCancellationReason.ConsumerAbandoned, "consumer_abandoned")]
    [Arguments((int)ServerCallCancellationReason.DeadlineExceeded, "deadline_exceeded")]
    [Arguments((int)ServerCallCancellationReason.ModuleDraining, "module_draining")]
    [Arguments((int)ServerCallCancellationReason.ServerStopping, "server_stopping")]
    [Arguments((int)ServerCallCancellationReason.ConnectionClosed, "connection_closed")]
    [Arguments((int)ServerCallCancellationReason.AdmissionResourceExhausted, "admission_resource_exhausted")]
    [Arguments((int)ServerCallCancellationReason.PreAdmissionStreamResourceExhausted,
        "pre_admission_stream_resource_exhausted")]
    [Arguments((int)ServerCallCancellationReason.Completed, "unknown")]
    [Arguments(byte.MaxValue, "unknown")]
    public async Task GetTerminationReasonTagShouldRemainLowCardinality(
        int reasonValue,
        string expectedTag)
    {
        var actual = ServerCallTerminationMapper.GetTerminationReasonTag(
            (ServerCallCancellationReason)reasonValue);

        await Assert.That(actual).IsEqualTo(expectedTag);
    }

    [Test]
    [Arguments((int)ProtocolV2CancelReason.Unspecified, (int)SharpLinkErrorCode.Cancelled,
        "Remote caller cancelled the RPC stream.")]
    [Arguments((int)ProtocolV2CancelReason.UserCancellation, (int)SharpLinkErrorCode.Cancelled,
        "Remote caller cancelled the RPC stream.")]
    [Arguments((int)ProtocolV2CancelReason.DeadlineExceeded, (int)SharpLinkErrorCode.DeadlineExceeded,
        "Remote RPC deadline exceeded.")]
    [Arguments((int)ProtocolV2CancelReason.ConsumerAbandoned, (int)SharpLinkErrorCode.Cancelled,
        "Remote consumer abandoned the RPC stream.")]
    [Arguments(byte.MaxValue, (int)SharpLinkErrorCode.Cancelled,
        "Remote caller cancelled the RPC stream.")]
    public async Task CreateRemoteCancellationExceptionShouldPreserveWireError(
        int reasonValue,
        int expectedCodeValue,
        string expectedMessage)
    {
        var exception = ServerCallTerminationMapper.CreateRemoteCancellationException(
            (ProtocolV2CancelReason)reasonValue);

        await Assert.That(exception.Code).IsEqualTo((SharpLinkErrorCode)expectedCodeValue);
        await Assert.That(exception.Message).IsEqualTo(expectedMessage);
    }

    [Test]
    [Arguments((int)ServerCallCancellationReason.None, (int)SharpLinkErrorCode.Cancelled,
        "Request canceled.")]
    [Arguments((int)ServerCallCancellationReason.RemoteCancel, (int)SharpLinkErrorCode.Cancelled,
        "Request canceled.")]
    [Arguments((int)ServerCallCancellationReason.ConsumerAbandoned, (int)SharpLinkErrorCode.Cancelled,
        "Request canceled.")]
    [Arguments((int)ServerCallCancellationReason.DeadlineExceeded, (int)SharpLinkErrorCode.DeadlineExceeded,
        "Request deadline exceeded.")]
    [Arguments((int)ServerCallCancellationReason.ModuleDraining, (int)SharpLinkErrorCode.Unavailable,
        "RPC module is draining")]
    [Arguments((int)ServerCallCancellationReason.ServerStopping, (int)SharpLinkErrorCode.Unavailable,
        "Server is stopping.")]
    [Arguments((int)ServerCallCancellationReason.ConnectionClosed, (int)SharpLinkErrorCode.ConnectionClosed,
        "Connection closed.")]
    [Arguments((int)ServerCallCancellationReason.AdmissionResourceExhausted,
        (int)SharpLinkErrorCode.ResourceExhausted,
        "Admission queue retained-byte capacity was exhausted.")]
    [Arguments((int)ServerCallCancellationReason.PreAdmissionStreamResourceExhausted,
        (int)SharpLinkErrorCode.ResourceExhausted,
        "\u000ePre-admission stream retained-byte capacity was exhausted.")]
    [Arguments((int)ServerCallCancellationReason.Completed, (int)SharpLinkErrorCode.Cancelled,
        "Request canceled.")]
    [Arguments(byte.MaxValue, (int)SharpLinkErrorCode.Cancelled, "Request canceled.")]
    public async Task CreateServerCancellationExceptionShouldPreserveEveryTermination(
        int reasonValue,
        int expectedCodeValue,
        string expectedMessage)
    {
        var exception = ServerCallTerminationMapper.CreateServerCancellationException(
            (ServerCallCancellationReason)reasonValue,
            deadlineExceeded: true);

        await Assert.That(exception.Code).IsEqualTo((SharpLinkErrorCode)expectedCodeValue);
        await Assert.That(exception.Message).IsEqualTo(expectedMessage);
    }

    [Test]
    public async Task PreAdmissionStreamExhaustionShouldKeepStableResourceReason()
    {
        var exception = ServerCallTerminationMapper.CreateServerCancellationException(
            ServerCallCancellationReason.PreAdmissionStreamResourceExhausted,
            deadlineExceeded: false);

        await Assert.That(SharpLinkResourceExhaustion.GetReason(exception))
            .IsEqualTo(SharpLinkResourceExhaustion.ServerPreAdmissionStreamBytes);
    }

    [Test]
    public async Task CreateServerCancellationExceptionShouldApplyStateBeforeDeadlineFallback()
    {
        var remoteWon = ServerCallTerminationMapper.CreateServerCancellationException(
            ServerCallCancellationReason.RemoteCancel,
            deadlineExceeded: true);
        var missingStateWithExpiredDeadline = ServerCallTerminationMapper.CreateServerCancellationException(
            reason: null,
            deadlineExceeded: true);
        var missingStateWithoutExpiredDeadline = ServerCallTerminationMapper.CreateServerCancellationException(
            reason: null,
            deadlineExceeded: false);

        await Assert.That(remoteWon.Code).IsEqualTo(SharpLinkErrorCode.Cancelled);
        await Assert.That(remoteWon.Message).IsEqualTo("Request canceled.");
        await Assert.That(missingStateWithExpiredDeadline.Code)
            .IsEqualTo(SharpLinkErrorCode.DeadlineExceeded);
        await Assert.That(missingStateWithExpiredDeadline.Message)
            .IsEqualTo("Request deadline exceeded.");
        await Assert.That(missingStateWithoutExpiredDeadline.Code)
            .IsEqualTo(SharpLinkErrorCode.Cancelled);
        await Assert.That(missingStateWithoutExpiredDeadline.Message)
            .IsEqualTo("Request canceled.");
    }

    private static TException CaptureException<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
            throw new Exception($"Expected {typeof(TException).Name}.");
        }
        catch (TException exception)
        {
            return exception;
        }
    }
}
