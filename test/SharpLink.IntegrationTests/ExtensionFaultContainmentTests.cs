namespace SharpLink.IntegrationTests;

[NotInParallel]
public sealed class ExtensionFaultContainmentTests
{
    [Test]
    public async Task ClientInterceptorBeforeNextShouldFailOnceReleaseStateAndReuseConnection()
    {
        var interceptor = new OneShotClientInterceptorFault(afterNext: false);
        await using var harness = await ExtensionFaultHarness.CreateAsync(new ExtensionFaultHarnessOptions
        {
            ClientInterceptors = [interceptor],
            SkipInitialSessionProbe = true
        });

        var failure = await CaptureFailureAsync(harness.Service.EchoAsync(10).AsTask());
        Ensure(failure is InvalidOperationException &&
               failure.Message.Contains("before next", StringComparison.Ordinal),
            "client interceptor before-next fault should remain the authoritative local failure");
        Ensure(interceptor.FailedContext is
        {
            Status: SharpLinkInvocationStatus.Failed,
            ErrorCode: SharpLinkErrorCode.Internal,
            Exception: InvalidOperationException
        }, "client interceptor context should be terminal Failed after unwind");
        await harness.AssertReusableAsync("client interceptor before next");
    }

    [Test]
    public async Task ClientInterceptorAfterNextAndNestedChainShouldFailOnceAndRemainReusable()
    {
        var outer = new RecordingClientInterceptor();
        var inner = new OneShotClientInterceptorFault(afterNext: true);
        var service = new ExtensionFaultService();
        await using var harness = await ExtensionFaultHarness.CreateAsync(new ExtensionFaultHarnessOptions
        {
            ServiceInstance = service,
            ClientInterceptors = [outer, inner],
            SkipInitialSessionProbe = true
        });
        var before = service.InvocationCount;

        var failure = await CaptureFailureAsync(harness.Service.EchoAsync(20).AsTask());
        Ensure(failure is InvalidOperationException &&
               failure.Message.Contains("after next", StringComparison.Ordinal),
            "client interceptor after-next fault should remain the authoritative local failure");
        Ensure(service.InvocationCount == before + 1,
            "after-next fault must execute the terminal RPC exactly once");
        Ensure(outer.Calls == 1, "nested outer interceptor should execute exactly once");
        Ensure(inner.FailedContext is { Status: SharpLinkInvocationStatus.Failed },
            "nested client context should be terminal Failed");
        await harness.AssertReusableAsync("client interceptor after next");
    }

    [Test]
    public async Task ServerInterceptorBeforeAndAfterNextShouldMapOnceAndReuseSameSession()
    {
        foreach (var afterNext in new[] { false, true })
        {
            var interceptor = new OneShotServerInterceptorFault(afterNext);
            await using var harness = await ExtensionFaultHarness.CreateAsync(new ExtensionFaultHarnessOptions
            {
                ServerInterceptors = [interceptor],
                SkipInitialSessionProbe = true
            });

            var failure = await CaptureFailureAsync(harness.Service.EchoAsync(30).AsTask());
            Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.FailedPrecondition },
                $"server interceptor {(afterNext ? "after" : "before")}-next public mapping");
            Ensure(interceptor.FailedContext is
            {
                Status: SharpLinkInvocationStatus.Failed,
                ErrorCode: SharpLinkErrorCode.FailedPrecondition
            }, "server interceptor context should be final and mapped");
            await harness.AssertReusableAsync(
                $"server interceptor {(afterNext ? "after" : "before")} next");
        }
    }

    [Test]
    public async Task AdmissionAcquireFailureShouldNotCreateLeaseOrPoisonNextCall()
    {
        var policy = new FaultingEndpointAdmissionPolicy(throwAcquireOnce: true);
        await using var harness = await ExtensionFaultHarness.CreateAsync(new ExtensionFaultHarnessOptions
        {
            EndpointAdmissionPolicy = policy,
            SkipInitialSessionProbe = true
        });

        var failure = await CaptureFailureAsync(harness.Service.EchoAsync(40).AsTask());
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.FailedPrecondition },
            "admission TryAcquire exception should map to FailedPrecondition");
        Ensure(policy.AcquireCount >= 1, "admission acquire should be invoked");
        Ensure(policy.ReportCount == 0,
            "failed admission acquire must not manufacture a successful lease report");
        await harness.AssertReusableAsync("admission acquire failure");
        Ensure(policy.ReportCount == 2,
            "session probe and healthy reuse should each report once after the failed acquire");
    }

    [Test]
    public async Task AdmissionReportAndLoggerFailuresShouldNotReplaceBusinessResultOrDoubleReport()
    {
        var policy = new FaultingEndpointAdmissionPolicy(throwReport: true);
        using var loggerFactory = new ThrowingLoggerFactory();
        await using var harness = await ExtensionFaultHarness.CreateAsync(new ExtensionFaultHarnessOptions
        {
            EndpointAdmissionPolicy = policy,
            LoggerFactory = loggerFactory
        });
        var reportsBefore = policy.ReportCount;

        var result = await harness.Service.EchoAsync(41).ConfigureAwait(false);
        Ensure(result == 42,
            "admission Report/logger observer failure must not replace a successful RPC result");
        await harness.AssertClientIdleAsync("admission report logger failure");
        Ensure(policy.ReportCount == reportsBefore + 1,
            "successful endpoint admission lease must be reported exactly once");

        await harness.AssertReusableAsync("admission report logger failure");
        Ensure(policy.ReportCount == reportsBefore + 3,
            "healthy session probe and reuse must each report once without stale lease state");
    }

    [Test]
    public async Task RetryPolicyFailureShouldNotManufactureAnotherAttemptAndClientShouldRecover()
    {
        var retry = new ThrowingRetryPolicy();
        var service = new ExtensionFaultService();
        await using var harness = await ExtensionFaultHarness.CreateAsync(new ExtensionFaultHarnessOptions
        {
            ServiceInstance = service,
            RetryPolicy = retry
        });
        var before = service.InvocationCount;

        var failure = await CaptureFailureAsync(harness.Service.FailOnceAsync().AsTask());
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.FailedPrecondition },
            "retry policy exception should map to FailedPrecondition");
        Ensure(retry.Calls == 1, "retry policy should be evaluated exactly once");
        Ensure(service.InvocationCount == before + 1,
            "retry policy failure must not manufacture a second RPC attempt");
        Ensure(await harness.Service.FailOnceAsync().ConfigureAwait(false) == 42,
            "next logical call should remain healthy after retry policy failure");
        await harness.AssertReusableAsync("retry policy failure");
    }

    [Test]
    public async Task CodecFaultsShouldReleasePendingStateAndKeepProtocolConnectionReusable()
    {
        var cases = new[]
        {
            new CodecCase("client request serialization", new OneShotFaultCodec(CodecFaultStage.Serialize), new OneShotFaultCodec(CodecFaultStage.None)),
            new CodecCase("server request deserialization", new OneShotFaultCodec(CodecFaultStage.None), new OneShotFaultCodec(CodecFaultStage.Deserialize)),
            new CodecCase("server response serialization", new OneShotFaultCodec(CodecFaultStage.None), new OneShotFaultCodec(CodecFaultStage.Serialize)),
            new CodecCase("client response deserialization", new OneShotFaultCodec(CodecFaultStage.Deserialize), new OneShotFaultCodec(CodecFaultStage.None))
        };

        foreach (var testCase in cases)
        {
            await using var harness = await ExtensionFaultHarness.CreateAsync(new ExtensionFaultHarnessOptions
            {
                ClientCodec = testCase.ClientCodec,
                ServerCodec = testCase.ServerCodec
            });

            var failure = await CaptureFailureAsync(harness.Service
                .EchoPayloadAsync(new ExtensionFaultPayload { Value = 10 })
                .AsTask());
            Ensure(failure is not null, $"{testCase.Name} should fail the injected RPC");
            await harness.AssertClientIdleAsync(testCase.Name);

            var recovered = await harness.Service
                .EchoPayloadAsync(new ExtensionFaultPayload { Value = 20 })
                .ConfigureAwait(false);
            Ensure(recovered.Value == 21, $"{testCase.Name} should not contaminate the next codec lifecycle");
            await harness.AssertReusableAsync(testCase.Name);
        }
    }

    [Test]
    public async Task ClientStreamMoveNextAndDisposeFailuresShouldReleasePendingAndProducerState()
    {
        foreach (var producer in new[]
                 {
                     new OneShotThrowingAsyncEnumerable(throwMoveNext: true, throwDispose: false),
                     new OneShotThrowingAsyncEnumerable(throwMoveNext: false, throwDispose: true)
                 })
        {
            await using var harness = await ExtensionFaultHarness.CreateAsync();
            var failure = await CaptureFailureAsync(harness.Service.UploadAsync(producer).AsTask());
            Ensure(failure is not null, "client stream producer/dispose injection should fail the call");
            await harness.AssertReusableAsync("client stream producer/dispose failure");
        }
    }

    [Test]
    public async Task ServerStreamProducerFailureAfterPartialOutputShouldReleaseDispatcherForReuse()
    {
        await using var harness = await ExtensionFaultHarness.CreateAsync(new ExtensionFaultHarnessOptions
        {
            ServiceInstance = new ExtensionFaultService(failStreamAfterFirst: true)
        });
        var stream = harness.Service.StreamAsync(3);
        await using var enumerator = stream.GetAsyncEnumerator();
        Ensure(await enumerator.MoveNextAsync().ConfigureAwait(false) && enumerator.Current == 0,
            "server stream should publish its first item before injected failure");
        var failure = await CaptureFailureAsync(enumerator.MoveNextAsync().AsTask());
        Ensure(failure is SharpLinkException,
            "server stream producer failure should become a structured stream terminal");
        await harness.AssertReusableAsync("server stream producer failure");
    }

    [Test]
    public async Task ServiceFactoryCreationAndDisposalFailuresShouldRollbackPerCallOwnership()
    {
        var creation = 0;
        await using (var harness = await ExtensionFaultHarness.CreateAsync(new ExtensionFaultHarnessOptions
        {
            ServiceFactory = _ =>
            {
                var index = Interlocked.Increment(ref creation);
                if (index == 2)
                    throw new InvalidOperationException("injected service factory failure");
                return new ExtensionFaultService();
            },
            ServiceLifetime = SharpLinkServiceLifetime.Call
        }))
        {
            var failure = await CaptureFailureAsync(harness.Service.EchoAsync(5).AsTask());
            Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.Internal },
                "service factory activation failure should be mapped to Internal");
            await harness.AssertReusableAsync("service factory creation failure");
        }

        var disposal = 0;
        await using (var harness = await ExtensionFaultHarness.CreateAsync(new ExtensionFaultHarnessOptions
        {
            ServiceFactory = _ => new ExtensionFaultService(
                throwOnDispose: Interlocked.Increment(ref disposal) == 2),
            ServiceLifetime = SharpLinkServiceLifetime.Call
        }))
        {
            var failure = await CaptureFailureAsync(harness.Service.EchoAsync(6).AsTask());
            Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.Internal },
                "service disposal failure should be mapped to Internal");
            await harness.AssertReusableAsync("service disposal failure");
        }
    }

    [Test]
    public async Task MeterListenerFaultShouldNotReplaceBusinessResultOrPoisonReuse()
    {
        await using var harness = await ExtensionFaultHarness.CreateAsync();
        using var listener = new ThrowingMeterScope("sharplink.calls.started");

        var result = await harness.Service.EchoAsync(41).ConfigureAwait(false);
        Ensure(result == 42, "MeterListener callback failure must not replace the business result");
        await harness.AssertReusableAsync("MeterListener callback failure");
    }

    [Test]
    public async Task ActivitySamplerFaultShouldNotReplaceBusinessResultOrPoisonReuse()
    {
        await using var harness = await ExtensionFaultHarness.CreateAsync();
        using var listener = new ThrowingActivityScope();

        var result = await harness.Service.EchoAsync(41).ConfigureAwait(false);
        Ensure(result == 42, "ActivityListener sampler failure must not replace the business result");
        await harness.AssertReusableAsync("ActivityListener sampler failure");
    }

    [Test]
    public async Task RepeatedFaultReuseShouldRemainCleanForOneHundredCycles()
    {
        var interceptor = new AlternatingClientInterceptorFault();
        await using var harness = await ExtensionFaultHarness.CreateAsync(new ExtensionFaultHarnessOptions
        {
            ClientInterceptors = [interceptor],
            SkipInitialSessionProbe = true
        });

        for (var cycle = 0; cycle < 100; cycle++)
        {
            if ((cycle & 1) == 0)
            {
                var failure = await CaptureFailureAsync(harness.Service.EchoAsync(cycle).AsTask());
                Ensure(failure is InvalidOperationException,
                    $"cycle {cycle}: injected interceptor failure should be observed");
            }
            else
            {
                Ensure(await harness.Service.EchoAsync(cycle).ConfigureAwait(false) == cycle + 1,
                    $"cycle {cycle}: healthy reuse result");
            }
            await harness.AssertClientIdleAsync($"repeated cycle {cycle}");
        }

        Ensure(interceptor.Faults == 50 && interceptor.Successes == 50,
            "100-cycle matrix should exercise exactly 50 injected failures and 50 successes");
        await harness.AssertReusableAsync("100-cycle repeated fault reuse");
    }

    private static async Task<Exception?> CaptureFailureAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"assert failed: {name}");
    }

    private readonly record struct CodecCase(
        string Name,
        IRpcCodec<ExtensionFaultPayload> ClientCodec,
        IRpcCodec<ExtensionFaultPayload> ServerCodec);

    private sealed class AlternatingClientInterceptorFault : ISharpLinkClientInterceptor
    {
        private int _calls;
        private int _faults;
        private int _successes;

        internal int Faults => Volatile.Read(ref _faults);
        internal int Successes => Volatile.Read(ref _successes);

        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            var call = Interlocked.Increment(ref _calls);
            if ((call & 1) == 0)
            {
                Interlocked.Increment(ref _successes);
                return next(context);
            }
            Interlocked.Increment(ref _faults);
            return ValueTask.FromException<SharpLinkClientInvocationResult>(
                new InvalidOperationException("injected alternating interceptor failure"));
        }
    }
}
