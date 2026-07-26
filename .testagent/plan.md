# 0.8.14 regression-test plan

1. [x] `UnixNamedPipeNormalizationShouldRespectUtf8PathBytes` covers native UTF-8 path budgeting.
2. [x] `NamedPipeListenerShouldRejectInvalidServerInstanceLimits` covers both invalid boundaries as one configuration defect.
3. [x] `ThrowingProducerCancellationCallbackShouldNotStrandCompletion` covers callback isolation, terminal preservation, and slot release.
4. [x] `SocketClientShouldRejectTheServerOnlyEphemeralPort` covers central remote-endpoint validation.
5. [x] `StreamCreditBlockedHeadShouldNotBlockAnEligibleStream` covers cross-stream progress without weakening connection-credit FIFO.
6. [x] Complete assertion/pseudo-mutation review, reversed performance A/B, final build/tests, package smoke, and documentation.
7. [x] Review the final diff and create the local 0.8.14 commit.
