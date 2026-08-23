from pathlib import Path

path = Path('src/SharpLink.Client/SharpLinkClient.Lifecycle.cs')
text = path.read_text()
old = '''                        if (header.Type == ProtocolV2FrameType.Response)
                            connection.PendingCalls.DispatchError(requestId, exception);
                            if (header.Type == ProtocolV2FrameType.StreamData)
                            {
                                if (!connection.PendingCalls.TryAcceptStreamData(requestId))
                                    continue;
                                var streamId = RpcSession.ReadCompressedStreamId(payload);
                            if (streamId == 0)
                            {
                                connection.PendingCalls.TryComplete(
                                    requestId,
                                    PendingCallCompletionReason.ConsumerAbandoned,
                                    exception);
                            }
                            else
                            {
                                session.StreamManager.CompleteStream(requestId, streamId, exception);
                            }
                        }
'''
new = '''                        if (header.Type == ProtocolV2FrameType.Response)
                            connection.PendingCalls.DispatchError(requestId, exception);
                        else if (header.Type == ProtocolV2FrameType.StreamData)
                        {
                            if (!connection.PendingCalls.TryAcceptStreamData(requestId))
                                continue;
                            var streamId = RpcSession.ReadCompressedStreamId(payload);
                            if (streamId == 0)
                            {
                                connection.PendingCalls.TryComplete(
                                    requestId,
                                    PendingCallCompletionReason.ConsumerAbandoned,
                                    exception);
                            }
                            else
                            {
                                session.StreamManager.CompleteStream(requestId, streamId, exception);
                            }
                        }
'''
assert text.count(old) == 1, 'unexpected lifecycle decode-error block'
path.write_text(text.replace(old, new, 1))
