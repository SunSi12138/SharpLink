namespace SharpLink.IntegrationTests;

public class InterceptorIntegrationTests
{
    [Test]
    public async Task ClientAndServerInterceptorsShouldObserveGeneratedContext()
    {
        var clientInterceptor = new RecordingClientInterceptor();
        var serverInterceptor = new RecordingServerInterceptor();
        await using var harness = await InterceptorHarness.CreateAsync(
            clientInterceptor: clientInterceptor,
            serverInterceptor: serverInterceptor);

        var service = harness.Client.Get<IInterceptorTestService>();
        var result = await service.DescribeAsync(17, default);

        Ensure(result.Contains("client-interceptor", StringComparison.Ordinal), "client metadata reached service");
        Ensure(clientInterceptor.Method.IsIdempotent, "client descriptor idempotent marker");
        Ensure(clientInterceptor.StatusAfterNext == SharpLinkInvocationStatus.Succeeded, "client status after next");
        Ensure(serverInterceptor.Context is { RequestId: > 0 }, "server request ID");
        Ensure(serverInterceptor.Context!.Method.IsIdempotent, "server descriptor idempotent marker");
        Ensure(serverInterceptor.Context.RemoteEndPoint is not null, "server peer endpoint");
        Ensure(serverInterceptor.StatusAfterNext == SharpLinkInvocationStatus.Succeeded, "server status after next");
    }

    [Test]
    public async Task ClientInterceptorShouldShortCircuitWithoutAConnection()
    {
        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), GetFreePort())
            .AddInterceptor(new ShortCircuitClientInterceptor(777))
            .Build();
        try
        {
            var service = client.Get<IInterceptorTestService>();
            Ensure(await service.DescribeNumberAsync(1) == 777, "short-circuit response");
            Ensure(client.State == SharpLinkConnectionState.Created, "short circuit should not connect");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task ServerInterceptorShouldRejectWithStructuredStatus()
    {
        await using var harness = await InterceptorHarness.CreateAsync(
            serverInterceptor: new RejectingServerInterceptor());
        var service = harness.Client.Get<IInterceptorTestService>();

        var exception = await CaptureSharpLinkException(service.DescribeNumberAsync(1).AsTask());
        Ensure(exception.Code == SharpLinkErrorCode.PermissionDenied, "server interceptor status");
        Ensure(exception.Message.Contains("policy", StringComparison.Ordinal), "server interceptor public message");
    }

    [Test]
    public async Task StructuredCancelledInterceptorFailuresShouldRecordCancelledStatus()
    {
        var clientInterceptor = new CancellingClientInterceptor();
        await using (var clientHarness = await InterceptorHarness.CreateAsync(
                         clientInterceptor: clientInterceptor))
        {
            var service = clientHarness.Client.Get<IInterceptorTestService>();
            var exception = await CaptureSharpLinkException(service.DescribeNumberAsync(1).AsTask());
            Ensure(exception.Code == SharpLinkErrorCode.Cancelled, "client structured cancellation code");
            Ensure(clientInterceptor.Context?.Status == SharpLinkInvocationStatus.Cancelled,
                "client structured cancellation status");
            Ensure(ReferenceEquals(clientInterceptor.Context?.Exception, exception),
                "client structured cancellation exception identity");
        }

        var serverInterceptor = new CancellingServerInterceptor();
        await using (var serverHarness = await InterceptorHarness.CreateAsync(
                         serverInterceptor: serverInterceptor))
        {
            var service = serverHarness.Client.Get<IInterceptorTestService>();
            var exception = await CaptureSharpLinkException(service.DescribeNumberAsync(1).AsTask());
            Ensure(exception.Code == SharpLinkErrorCode.Cancelled, "server structured cancellation code");
            Ensure(serverInterceptor.Context?.Status == SharpLinkInvocationStatus.Cancelled,
                "server structured cancellation status");
            Ensure(serverInterceptor.Context?.ErrorCode == SharpLinkErrorCode.Cancelled,
                "server structured cancellation context code");
        }
    }

    [Test]
    public async Task ServerInterceptorShouldObserveTerminalFailureBeforeUnwind()
    {
        var interceptor = new FailureObservingServerInterceptor();
        await using var harness = await InterceptorHarness.CreateAsync(serverInterceptor: interceptor);
        var service = harness.Client.Get<IInterceptorTestService>();

        var failure = await CaptureSharpLinkException(service.FailAsync().AsTask());

        Ensure(failure.Code == SharpLinkErrorCode.Internal, "mapped service failure code");
        Ensure(interceptor.StatusAtCatch == SharpLinkInvocationStatus.Failed,
            "server context status before interceptor unwind");
        Ensure(interceptor.ErrorCodeAtCatch == SharpLinkErrorCode.Internal,
            "server context code before interceptor unwind");
        Ensure(interceptor.ExceptionAtCatch is InvalidOperationException,
            "server context exception before interceptor unwind");
    }

    [Test]
    public async Task ServerInterceptorShouldRetainMappedStreamFailureAfterNextReturns()
    {
        var interceptor = new RecordingServerInterceptor();
        await using var harness = await InterceptorHarness.CreateAsync(serverInterceptor: interceptor);
        var stream = harness.Client.Get<IInterceptorTestService>().FailStreamAsync().GetAsyncEnumerator();
        try
        {
            Ensure(await stream.MoveNextAsync() && stream.Current == 1, "intercepted stream first item");
            var failure = await CaptureSharpLinkException(stream.MoveNextAsync().AsTask());
            Ensure(failure.Code == SharpLinkErrorCode.Internal, "intercepted stream wire status");
        }
        finally
        {
            await stream.DisposeAsync();
        }
        await interceptor.Completed.WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(interceptor.StatusAfterNext == SharpLinkInvocationStatus.Failed,
            "a mapped stream failure must not be overwritten with Succeeded after the bridge returns");
        Ensure(interceptor.Context?.ErrorCode == SharpLinkErrorCode.Internal,
            "the interceptor context must retain the mapped stream code");
        Ensure(interceptor.Context?.Exception is InvalidOperationException,
            "the interceptor context must retain the original service stream exception");
    }

    [Test]
    public async Task ResponseServerInterceptorMustInvokeItsContinuation()
    {
        var interceptor = new MissingNextServerInterceptor();
        await using var harness = await InterceptorHarness.CreateAsync(serverInterceptor: interceptor);
        var service = harness.Client.Get<IInterceptorTestService>();

        var failure = await CaptureSharpLinkException(service.DescribeNumberAsync(1).AsTask());

        Ensure(failure.Code == SharpLinkErrorCode.Internal,
            "a response interceptor that omits next must fail on the server");
        Ensure(interceptor.Context?.Status == SharpLinkInvocationStatus.Failed,
            "missing response continuation status");
        Ensure(interceptor.Context?.Exception is InvalidOperationException,
            "missing response continuation exception");
    }

    [Test]
    public async Task WrongClientShortCircuitTypeMustRecordFailure()
    {
        var interceptor = new WrongTypeClientInterceptor();
        await using var harness = await InterceptorHarness.CreateAsync(clientInterceptor: interceptor);
        var service = harness.Client.Get<IInterceptorTestService>();

        var failure = await CaptureException(service.DescribeNumberAsync(1).AsTask());

        Ensure(failure is InvalidCastException, "wrong short-circuit result type");
        Ensure(interceptor.Context?.Status == SharpLinkInvocationStatus.Failed,
            "wrong short-circuit context status");
        Ensure(interceptor.Context?.ErrorCode == SharpLinkErrorCode.Internal,
            "wrong short-circuit context code");
        Ensure(ReferenceEquals(interceptor.Context?.Exception, failure),
            "wrong short-circuit exception identity");
    }

    [Test]
    public async Task ClientShortCircuitValidationMustCoverStreamAndOneWayShapes()
    {
        var wrongStream = new TrackingShortCircuitClientInterceptor("not-a-stream");
        await using (var client = CreateDisconnectedClient(wrongStream))
        {
            var stream = client.Get<IInterceptorTestService>().FailStreamAsync().GetAsyncEnumerator();
            try
            {
                var failure = await CaptureException(stream.MoveNextAsync().AsTask());
                Ensure(failure is InvalidCastException, "wrong streaming short-circuit result type");
                Ensure(wrongStream.Context?.Status == SharpLinkInvocationStatus.Failed,
                    "wrong streaming short-circuit context status");
            }
            finally
            {
                await stream.DisposeAsync();
            }
        }

        var wrongOneWay = new TrackingShortCircuitClientInterceptor(42);
        await using (var client = CreateDisconnectedClient(wrongOneWay))
        {
            var failure = await CaptureException(
                client.Get<IInterceptorTestService>().NotifyAsync(1).AsTask());
            Ensure(failure is InvalidCastException, "non-null OneWay short-circuit result");
            Ensure(wrongOneWay.Context?.Status == SharpLinkInvocationStatus.Failed,
                "non-null OneWay short-circuit context status");
        }

        var validStream = new TrackingShortCircuitClientInterceptor(ShortCircuitValues());
        await using (var client = CreateDisconnectedClient(validStream))
        {
            var values = new List<int>();
            await foreach (var value in client.Get<IInterceptorTestService>().FailStreamAsync())
                values.Add(value);
            Ensure(values is [42], "valid streaming short circuit");
            Ensure(validStream.Context?.Status == SharpLinkInvocationStatus.Succeeded,
                "valid streaming short-circuit context status");
            Ensure(client.State == SharpLinkConnectionState.Created,
                "valid streaming short circuit should not connect");
        }

        var validOneWay = new TrackingShortCircuitClientInterceptor(null);
        await using (var client = CreateDisconnectedClient(validOneWay))
        {
            await client.Get<IInterceptorTestService>().NotifyAsync(1);
            Ensure(validOneWay.Context?.Status == SharpLinkInvocationStatus.Succeeded,
                "valid OneWay short-circuit context status");
            Ensure(client.State == SharpLinkConnectionState.Created,
                "valid OneWay short circuit should not connect");
        }
    }

    [Test]
    public async Task ClientStreamConsumerMustNotCaptureCallerSynchronizationContext()
    {
        await using var harness = await InterceptorHarness.CreateAsync();
        var service = harness.Client.Get<IInterceptorTestService>();
        var stream = new ControlledClientStream(42);
        var context = new CountingSynchronizationContext();
        Task<int> call;
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            call = service.SumStreamAsync(stream, CancellationToken.None).AsTask();
            Ensure(stream.MoveNextStarted.IsCompleted,
                "client stream production should reach the first asynchronous MoveNext synchronously");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        stream.ReleaseFirstMoveNext();
        Ensure(await call.WaitAsync(TimeSpan.FromSeconds(3)) == 42, "client stream sum");
        Ensure(context.PostCount == 0,
            $"framework client-stream enumeration posted {context.PostCount} continuation(s) to the caller context");
    }

    [Test]
    [NotInParallel]
    public async Task ServerInterceptorMustJoinAnInvokedContinuation()
    {
        InterceptorTestService.ResetDelayedCall();
        await using var harness = await InterceptorHarness.CreateAsync(
            serverInterceptor: new AbandoningServerInterceptor());
        var call = harness.Client.Get<IInterceptorTestService>().DelayedAsync().AsTask();
        try
        {
            await InterceptorTestService.DelayedCallStarted.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(50);
            Ensure(!call.IsCompleted, "server interceptor must not abandon its invoked continuation");
        }
        finally
        {
            InterceptorTestService.ReleaseDelayedCall();
        }
        Ensure(await call.WaitAsync(TimeSpan.FromSeconds(3)) == 42,
            "joined server continuation response");
    }

    [Test]
    [NotInParallel]
    public async Task ClientInterceptorMustJoinAnInvokedContinuation()
    {
        InterceptorTestService.ResetDelayedCall();
        await using var harness = await InterceptorHarness.CreateAsync(
            clientInterceptor: new AbandoningClientInterceptor(777));
        var call = harness.Client.Get<IInterceptorTestService>().DelayedAsync().AsTask();
        try
        {
            await InterceptorTestService.DelayedCallStarted.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(50);
            Ensure(!call.IsCompleted, "client interceptor must not orphan its invoked continuation");
        }
        finally
        {
            InterceptorTestService.ReleaseDelayedCall();
        }
        Ensure(await call.WaitAsync(TimeSpan.FromSeconds(3)) == 777,
            "joined client continuation may still transform the result");
    }

    [Test]
    public async Task AsyncBeforeNextClientInterceptorShouldSuspendBeforeNext()
    {
        var interceptor = new AsyncBeforeNextBoundaryClientInterceptor();
        await using var harness = await InterceptorHarness.CreateAsync(clientInterceptor: interceptor);
        var service = harness.Client.Get<IInterceptorTestService>();

        Ensure(await service.DescribeNumberAsync(41) == 42, "async-before-next unary result");
        Ensure(interceptor.Context?.Status == SharpLinkInvocationStatus.Succeeded,
            "async-before-next context status");
    }

    [Test]
    public async Task AsyncAfterNextClientInterceptorShouldSuspendAfterNext()
    {
        var interceptor = new AsyncAfterNextBoundaryClientInterceptor();
        await using var harness = await InterceptorHarness.CreateAsync(clientInterceptor: interceptor);
        var service = harness.Client.Get<IInterceptorTestService>();

        Ensure(await service.DescribeNumberAsync(41) == 42, "async-after-next unary result");
        Ensure(interceptor.Context?.Status == SharpLinkInvocationStatus.Succeeded,
            "async-after-next context status");
    }

    [Test]
    public async Task AsyncBeforeAndAfterClientInterceptorShouldCoverOneWaySlowPath()
    {
        var interceptor = new AsyncBeforeAndAfterBoundaryClientInterceptor();
        await using var harness = await InterceptorHarness.CreateAsync(clientInterceptor: interceptor);
        var service = harness.Client.Get<IInterceptorTestService>();

        await service.NotifyAsync(17);

        Ensure(interceptor.Context?.Status == SharpLinkInvocationStatus.Succeeded,
            "async one-way context status");
        Ensure(interceptor.Method.Kind == RpcMethodKind.OneWay, "async one-way descriptor shape");
    }

    [Test]
    public async Task NonNullableResponsesMustRejectNullAtEveryGeneratedBoundary()
    {
        await using var harness = await InterceptorHarness.CreateAsync();
        var service = harness.Client.Get<IInterceptorTestService>();

        var unary = await CaptureSharpLinkException(service.RequiredNullAsync().AsTask());
        Ensure(unary.Code == SharpLinkErrorCode.Internal, "required unary null response");
        Ensure(await service.OptionalNullAsync() is null, "optional unary null response");

        var requiredStream = service.RequiredNullStreamAsync().GetAsyncEnumerator();
        try
        {
            var streamFailure = await CaptureSharpLinkException(requiredStream.MoveNextAsync().AsTask());
            Ensure(streamFailure.Code == SharpLinkErrorCode.Internal, "required stream null response");
        }
        finally
        {
            await requiredStream.DisposeAsync();
        }

        var optionalStream = service.OptionalNullStreamAsync().GetAsyncEnumerator();
        try
        {
            Ensure(await optionalStream.MoveNextAsync() && optionalStream.Current is null,
                "optional stream null response");
        }
        finally
        {
            await optionalStream.DisposeAsync();
        }

        Ensure(await service.CountOptionalStreamAsync(OptionalNullInput(), CancellationToken.None) == 1,
            "optional Client stream null request item");

        var requiredShortCircuit = new TrackingShortCircuitClientInterceptor(null);
        await using (var client = CreateDisconnectedClient(requiredShortCircuit))
        {
            var failure = await CaptureException(
                client.Get<IInterceptorTestService>().RequiredNullAsync().AsTask());
            Ensure(failure is InvalidCastException, "required Client short-circuit null response");
            Ensure(requiredShortCircuit.Context?.Status == SharpLinkInvocationStatus.Failed,
                "required Client short-circuit null status");
        }

        var optionalShortCircuit = new TrackingShortCircuitClientInterceptor(null);
        await using (var client = CreateDisconnectedClient(optionalShortCircuit))
        {
            Ensure(await client.Get<IInterceptorTestService>().OptionalNullAsync() is null,
                "optional Client short-circuit null response");
            Ensure(optionalShortCircuit.Context?.Status == SharpLinkInvocationStatus.Succeeded,
                "optional Client short-circuit null status");
        }
    }

    [Test]
    public async Task UndefinedMappedErrorCodeMustFallBackToInternal()
    {
        await using var harness = await InterceptorHarness.CreateAsync(
            exceptionMapper: new UndefinedCodeExceptionMapper());
        var failure = await CaptureSharpLinkException(
            harness.Client.Get<IInterceptorTestService>().FailAsync().AsTask());

        Ensure(failure.Code == SharpLinkErrorCode.Internal,
            "undefined mapper code must use the safe Internal boundary");
    }

    [Test]
    public async Task AsyncServerInterceptorShouldOwnArgumentsUntilNextCompletes()
    {
        var interceptor = new DelayedFirstServerInterceptor();
        await using var harness = await InterceptorHarness.CreateAsync(serverInterceptor: interceptor);
        var service = harness.Client.Get<IInterceptorTestService>();

        var delayed = service.DescribeNumberAsync(123_456).AsTask();
        await interceptor.Entered.WaitAsync(TimeSpan.FromSeconds(3));

        var churn = new Task<int>[256];
        for (var index = 0; index < churn.Length; index++)
            churn[index] = service.DescribeNumberAsync(index).AsTask();

        interceptor.Release();
        Ensure(await delayed.WaitAsync(TimeSpan.FromSeconds(3)) == 123_457, "delayed arguments remain owned");
        var values = await Task.WhenAll(churn).WaitAsync(TimeSpan.FromSeconds(5));
        for (var index = 0; index < values.Length; index++)
            Ensure(values[index] == index + 1, $"churn response {index}");
    }

    [Test]
    public async Task InterceptorContinuationShouldExecuteEachTerminalAtMostOnce()
    {
        InterceptorTestService.ResetInvocationCount();
        Exception? clientFailure;
        await using (var clientHarness = await InterceptorHarness.CreateAsync(
                         clientInterceptor: new DoubleNextClientInterceptor()))
        {
            var service = clientHarness.Client.Get<IInterceptorTestService>();
            clientFailure = await CaptureException(service.CountInvocationAsync().AsTask());
        }
        var clientInvocationCount = InterceptorTestService.InvocationCount;

        InterceptorTestService.ResetInvocationCount();
        Exception? serverFailure;
        await using (var serverHarness = await InterceptorHarness.CreateAsync(
                         serverInterceptor: new DoubleNextServerInterceptor()))
        {
            var service = serverHarness.Client.Get<IInterceptorTestService>();
            serverFailure = await CaptureException(service.CountInvocationAsync().AsTask());
        }
        var serverInvocationCount = InterceptorTestService.InvocationCount;

        Ensure(clientInvocationCount == 1 && serverInvocationCount == 1,
            $"continuations must execute one terminal each; client={clientInvocationCount}, server={serverInvocationCount}");
        Ensure(clientFailure is InvalidOperationException,
            "duplicate client continuation should fail locally");
        Ensure(serverFailure is SharpLinkException { Code: SharpLinkErrorCode.Internal },
            "duplicate server continuation should return a structured internal failure");
    }

    [Test]
    public async Task DefaultExceptionMapperShouldHideServiceDetails()
    {
        await using var harness = await InterceptorHarness.CreateAsync();
        var service = harness.Client.Get<IInterceptorTestService>();

        var exception = await CaptureSharpLinkException(service.FailAsync().AsTask());
        Ensure(exception.Code == SharpLinkErrorCode.Internal, "default mapper status");
        Ensure(!exception.Message.Contains("secret-service-detail", StringComparison.Ordinal), "default mapper hides detail");

        var stream = service.FailStreamAsync().GetAsyncEnumerator();
        try
        {
            Ensure(await stream.MoveNextAsync() && stream.Current == 1, "default stream first item");
            var streamFailure = await CaptureSharpLinkException(stream.MoveNextAsync().AsTask());
            Ensure(streamFailure.Code == SharpLinkErrorCode.Internal, "default stream mapper status");
            Ensure(!streamFailure.Message.Contains("secret-service-detail", StringComparison.Ordinal),
                "default stream mapper hides detail");
        }
        finally
        {
            await stream.DisposeAsync();
        }
    }

    [Test]
    public async Task DefaultExceptionMapperShouldPreserveStructuredServiceFailure()
    {
        await using var harness = await InterceptorHarness.CreateAsync();
        var service = harness.Client.Get<IInterceptorTestService>();

        var unary = await CaptureSharpLinkException(service.FailMappedAsync().AsTask());
        Ensure(unary is { Code: SharpLinkErrorCode.ResourceExhausted, Message: "public-structured" },
            "the default mapper must preserve an already structured unary failure");

        var stream = service.FailMappedStreamAsync().GetAsyncEnumerator();
        try
        {
            Ensure(await stream.MoveNextAsync() && stream.Current == 1, "structured stream first item");
            var streamFailure = await CaptureSharpLinkException(stream.MoveNextAsync().AsTask());
            Ensure(streamFailure is { Code: SharpLinkErrorCode.ResourceExhausted, Message: "public-structured" },
                "the default mapper must preserve an already structured stream failure");
        }
        finally
        {
            await stream.DisposeAsync();
        }
    }

    [Test]
    public async Task ServerStreamCancellationShouldPreserveDeadlineAndCallerReason()
    {
        await using var harness = await InterceptorHarness.CreateAsync(
            requestTimeout: TimeSpan.FromMilliseconds(150));
        var service = harness.Client.Get<IInterceptorTestService>();

        var deadlineStream = service.WaitStreamAsync(CancellationToken.None).GetAsyncEnumerator();
        try
        {
            Ensure(await deadlineStream.MoveNextAsync() && deadlineStream.Current == 1,
                "deadline stream first item");
            var deadlineFailure = await CaptureSharpLinkException(deadlineStream.MoveNextAsync().AsTask());
            Ensure(deadlineFailure.Code == SharpLinkErrorCode.DeadlineExceeded,
                "server stream timeout must retain DeadlineExceeded");
        }
        finally
        {
            await deadlineStream.DisposeAsync();
        }

        using var cancellation = new CancellationTokenSource();
        var cancelledStream = service.WaitStreamAsync(cancellation.Token).GetAsyncEnumerator();
        try
        {
            Ensure(await cancelledStream.MoveNextAsync() && cancelledStream.Current == 1,
                "caller-cancelled stream first item");
            cancellation.Cancel();
            await EnsureOperationCancelled(cancelledStream.MoveNextAsync().AsTask());
        }
        finally
        {
            await cancelledStream.DisposeAsync();
        }

        Ensure(await service.DescribeNumberAsync(41) == 42,
            "stream cancellation terminals must leave the connection usable");
    }

    [Test]
    public async Task DetailedErrorsShouldRequireExplicitOptIn()
    {
        await using var harness = await InterceptorHarness.CreateAsync(enableDetailedErrors: true);
        var service = harness.Client.Get<IInterceptorTestService>();

        var exception = await CaptureSharpLinkException(service.FailAsync().AsTask());
        Ensure(exception.Code == SharpLinkErrorCode.Internal, "detailed mapper status");
        Ensure(exception.Message == "secret-service-detail", "detailed mapper message");
    }

    [Test]
    public async Task CustomExceptionMapperShouldMapUnaryAndStreamingFailures()
    {
        await using var harness = await InterceptorHarness.CreateAsync(
            exceptionMapper: new TestExceptionMapper());
        var service = harness.Client.Get<IInterceptorTestService>();

        var unary = await CaptureSharpLinkException(service.FailAsync().AsTask());
        Ensure(unary.Code == SharpLinkErrorCode.FailedPrecondition, "custom unary status");
        Ensure(unary.Message == "public-failure", "custom unary message");

        var clientStream = await CaptureSharpLinkException(
            service.FailClientStreamAsync(FailureInput(), CancellationToken.None).AsTask());
        Ensure(clientStream.Code == SharpLinkErrorCode.FailedPrecondition, "custom client-stream status");
        Ensure(clientStream.Message == "public-failure", "custom client-stream message");

        var stream = service.FailStreamAsync().GetAsyncEnumerator();
        try
        {
            Ensure(await stream.MoveNextAsync() && stream.Current == 1, "stream first item");
            var streamFailure = await CaptureSharpLinkException(stream.MoveNextAsync().AsTask());
            Ensure(streamFailure.Code == SharpLinkErrorCode.FailedPrecondition, "custom stream status");
            Ensure(streamFailure.Message == "public-failure", "custom stream message");
        }
        finally
        {
            await stream.DisposeAsync();
        }

        var duplex = service.FailDuplexAsync(FailureInput(), CancellationToken.None).GetAsyncEnumerator();
        try
        {
            Ensure(await duplex.MoveNextAsync() && duplex.Current == 42, "duplex first item");
            var duplexFailure = await CaptureSharpLinkException(duplex.MoveNextAsync().AsTask());
            Ensure(duplexFailure.Code == SharpLinkErrorCode.FailedPrecondition, "custom duplex status");
            Ensure(duplexFailure.Message == "public-failure", "custom duplex message");
        }
        finally
        {
            await duplex.DisposeAsync();
        }
    }

    [Test]
    public async Task ThrowingExceptionMapperShouldFallBackAndKeepConnectionUsable()
    {
        var mapper = new ThrowingExceptionMapper();
        await using var harness = await InterceptorHarness.CreateAsync(exceptionMapper: mapper);
        var service = harness.Client.Get<IInterceptorTestService>();

        var unary = await CaptureSharpLinkException(service.FailAsync().AsTask());
        var stream = service.FailStreamAsync().GetAsyncEnumerator();
        SharpLinkException streamFailure;
        try
        {
            Ensure(await stream.MoveNextAsync() && stream.Current == 1, "throwing mapper stream first item");
            streamFailure = await CaptureSharpLinkException(stream.MoveNextAsync().AsTask());
        }
        finally
        {
            await stream.DisposeAsync();
        }

        Ensure(unary is { Code: SharpLinkErrorCode.Internal, Message: "Internal service error." },
            "a throwing unary mapper must use the safe structured fallback");
        Ensure(streamFailure is { Code: SharpLinkErrorCode.Internal, Message: "Internal service error." },
            "a throwing stream mapper must use the same safe structured fallback");
        Ensure(mapper.CallCount == 2, "both unary and stream failures must reach the configured mapper once");
        Ensure(await service.DescribeNumberAsync(41) == 42,
            "mapper failure must not terminate or poison the owning connection");
    }

    private static async Task<SharpLinkException> CaptureSharpLinkException(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            throw new Exception("assert failed: expected SharpLinkException");
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
    }

    private static async Task EnsureOperationCancelled(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            throw new Exception("assert failed: expected OperationCanceledException");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<Exception?> CaptureException(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static ISharpLinkClient CreateDisconnectedClient(ISharpLinkClientInterceptor interceptor)
        => SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), GetFreePort())
            .AddInterceptor(interceptor)
            .Build();

    private static async IAsyncEnumerable<int> ShortCircuitValues()
    {
        await Task.Yield();
        yield return 42;
    }

    private static async IAsyncEnumerable<int> FailureInput()
    {
        yield return 42;
        await Task.Yield();
    }

    private static async IAsyncEnumerable<string?> OptionalNullInput()
    {
        await Task.Yield();
        yield return null;
    }

    private static void Ensure(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"assert failed: {name}");
    }

    private sealed class RecordingClientInterceptor : ISharpLinkClientInterceptor
    {
        public RpcMethodDescriptor Method { get; private set; }
        public SharpLinkInvocationStatus StatusAfterNext { get; private set; }

        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            Method = context.Method;
            context.Options = context.Options with
            {
                Metadata = new SharpLinkMetadata(
                    new KeyValuePair<string, string>("source", "client-interceptor"))
            };
            var result = await next(context);
            StatusAfterNext = context.Status;
            return result;
        }
    }

    private sealed class AsyncBeforeNextBoundaryClientInterceptor : ISharpLinkClientInterceptor
    {
        public SharpLinkClientInvocationContext? Context { get; private set; }
        public RpcMethodDescriptor Method { get; private set; }

        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            Context = context;
            Method = context.Method;
            await Task.Yield();
            return await next(context).ConfigureAwait(false);
        }
    }

    private sealed class AsyncAfterNextBoundaryClientInterceptor : ISharpLinkClientInterceptor
    {
        public SharpLinkClientInvocationContext? Context { get; private set; }

        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            Context = context;
            var result = await next(context).ConfigureAwait(false);
            await Task.Yield();
            return result;
        }
    }

    private sealed class AsyncBeforeAndAfterBoundaryClientInterceptor : ISharpLinkClientInterceptor
    {
        public SharpLinkClientInvocationContext? Context { get; private set; }
        public RpcMethodDescriptor Method { get; private set; }

        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            Context = context;
            Method = context.Method;
            await Task.Yield();
            var result = await next(context).ConfigureAwait(false);
            await Task.Yield();
            return result;
        }
    }

    private sealed class ShortCircuitClientInterceptor(int value) : ISharpLinkClientInterceptor
    {
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
            => ValueTask.FromResult(new SharpLinkClientInvocationResult(value));
    }

    private sealed class CancellingClientInterceptor : ISharpLinkClientInterceptor
    {
        public SharpLinkClientInvocationContext? Context { get; private set; }

        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            Context = context;
            return ValueTask.FromException<SharpLinkClientInvocationResult>(new SharpLinkException(
                SharpLinkErrorCode.Cancelled,
                "cancelled by client interceptor"));
        }
    }

    private sealed class WrongTypeClientInterceptor : ISharpLinkClientInterceptor
    {
        public SharpLinkClientInvocationContext? Context { get; private set; }

        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            Context = context;
            return ValueTask.FromResult(new SharpLinkClientInvocationResult("not-an-int"));
        }
    }

    private sealed class TrackingShortCircuitClientInterceptor(object? value) : ISharpLinkClientInterceptor
    {
        public SharpLinkClientInvocationContext? Context { get; private set; }

        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            Context = context;
            return ValueTask.FromResult(new SharpLinkClientInvocationResult(value));
        }
    }

    private sealed class DoubleNextClientInterceptor : ISharpLinkClientInterceptor
    {
        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            var result = await next(context).ConfigureAwait(false);
            _ = await next(context).ConfigureAwait(false);
            return result;
        }
    }

    private sealed class AbandoningClientInterceptor(int value) : ISharpLinkClientInterceptor
    {
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            _ = next(context);
            return ValueTask.FromResult(new SharpLinkClientInvocationResult(value));
        }
    }

    private sealed class RecordingServerInterceptor : ISharpLinkServerInterceptor
    {
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SharpLinkServerInvocationContext? Context { get; private set; }
        public SharpLinkInvocationStatus StatusAfterNext { get; private set; }
        public Task Completed => _completed.Task;

        public async ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
        {
            Context = context;
            try
            {
                await next(context);
                StatusAfterNext = context.Status;
            }
            finally
            {
                _completed.TrySetResult();
            }
        }
    }

    private sealed class RejectingServerInterceptor : ISharpLinkServerInterceptor
    {
        public ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
            => ValueTask.FromException(new SharpLinkException(
                SharpLinkErrorCode.PermissionDenied,
                "Rejected by policy."));
    }

    private sealed class CancellingServerInterceptor : ISharpLinkServerInterceptor
    {
        public SharpLinkServerInvocationContext? Context { get; private set; }

        public ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
        {
            Context = context;
            return ValueTask.FromException(new SharpLinkException(
                SharpLinkErrorCode.Cancelled,
                "cancelled by server interceptor"));
        }
    }

    private sealed class FailureObservingServerInterceptor : ISharpLinkServerInterceptor
    {
        public SharpLinkInvocationStatus StatusAtCatch { get; private set; }
        public SharpLinkErrorCode? ErrorCodeAtCatch { get; private set; }
        public Exception? ExceptionAtCatch { get; private set; }

        public async ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
        {
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch
            {
                StatusAtCatch = context.Status;
                ErrorCodeAtCatch = context.ErrorCode;
                ExceptionAtCatch = context.Exception;
                throw;
            }
        }
    }

    private sealed class MissingNextServerInterceptor : ISharpLinkServerInterceptor
    {
        public SharpLinkServerInvocationContext? Context { get; private set; }

        public ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
        {
            Context = context;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;

        public int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _postCount);
            ThreadPool.QueueUserWorkItem(
                static work =>
                {
                    var item = ((SendOrPostCallback Callback, object? State))work!;
                    item.Callback(item.State);
                },
                (callback, state));
        }
    }

    private sealed class ControlledClientStream(int value) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private readonly TaskCompletionSource<bool> _firstMoveNext =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _moveNextStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _moveNextCount;

        public Task MoveNextStarted => _moveNextStarted.Task;
        public int Current { get; private set; }

        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => this;

        public ValueTask<bool> MoveNextAsync()
        {
            if (Interlocked.Increment(ref _moveNextCount) != 1)
                return ValueTask.FromResult(false);
            Current = value;
            _moveNextStarted.TrySetResult(true);
            return new ValueTask<bool>(_firstMoveNext.Task);
        }

        public void ReleaseFirstMoveNext() => _firstMoveNext.TrySetResult(true);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DoubleNextServerInterceptor : ISharpLinkServerInterceptor
    {
        public async ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
        {
            await next(context).ConfigureAwait(false);
            await next(context).ConfigureAwait(false);
        }
    }

    private sealed class AbandoningServerInterceptor : ISharpLinkServerInterceptor
    {
        public ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
        {
            _ = next(context);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DelayedFirstServerInterceptor : ISharpLinkServerInterceptor
    {
        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _first;

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult(true);

        public async ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
        {
            if (Interlocked.CompareExchange(ref _first, 1, 0) == 0)
            {
                _entered.TrySetResult(true);
                await _release.Task.ConfigureAwait(false);
            }
            await next(context).ConfigureAwait(false);
        }
    }

    private sealed class TestExceptionMapper : IRpcExceptionMapper
    {
        public SharpLinkException Map(Exception exception, SharpLinkServerInvocationContext context)
            => exception is InvalidOperationException
                ? new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "public-failure", exception)
                : new SharpLinkException(SharpLinkErrorCode.Internal, "internal", exception);
    }

    private sealed class UndefinedCodeExceptionMapper : IRpcExceptionMapper
    {
        public SharpLinkException Map(Exception exception, SharpLinkServerInvocationContext context)
            => new((SharpLinkErrorCode)int.MaxValue, "undefined", exception);
    }

    private sealed class ThrowingExceptionMapper : IRpcExceptionMapper
    {
        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);

        public SharpLinkException Map(Exception exception, SharpLinkServerInvocationContext context)
        {
            Interlocked.Increment(ref _callCount);
            throw new InvalidOperationException("mapper failed before returning a protocol error", exception);
        }
    }

    private sealed class InterceptorHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;
        public ISharpLinkClient Client { get; }

        private InterceptorHarness(
            CancellationTokenSource serverCts,
            Task serverTask,
            ISharpLinkServer server,
            ISharpLinkClient client)
        {
            _serverCts = serverCts;
            _serverTask = serverTask;
            _server = server;
            Client = client;
        }

        public static async Task<InterceptorHarness> CreateAsync(
            ISharpLinkClientInterceptor? clientInterceptor = null,
            ISharpLinkServerInterceptor? serverInterceptor = null,
            IRpcExceptionMapper? exceptionMapper = null,
            bool enableDetailedErrors = false,
            TimeSpan? requestTimeout = null)
        {
            var cts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseTcp(0, IPAddress.Loopback.ToString())

                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
            if (serverInterceptor is not null)
                serverBuilder.AddInterceptor(serverInterceptor);
            if (exceptionMapper is not null)
                serverBuilder.UseExceptionMapper(exceptionMapper);
            if (enableDetailedErrors)
                serverBuilder.EnableDetailedErrors();

            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = Task.Run(() => server.RunAsync(cts.Token).AsTask(), CancellationToken.None);

            var clientBuilder = SharpClientBuilder.Create()
                .UseTcp(IPAddress.Loopback.ToString(), port)

                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
            if (clientInterceptor is not null)
                clientBuilder.AddInterceptor(clientInterceptor);
            if (requestTimeout is { } timeout)
                clientBuilder.UseRequestTimeout(timeout);
            var client = clientBuilder.Build();
            await client.ConnectAsync(cts.Token);
            return new InterceptorHarness(cts, serverTask, server, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _serverCts.CancelAsync();
            await _server.DisposeAsync();
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            _serverCts.Dispose();
        }
    }
}

[RpcContract]
public interface IInterceptorTestService : IService
{
    [Idempotent]
    [NonCancellable]
    ValueTask<string> DescribeAsync(int value, SharpLinkCallOptions options);
    [NonCancellable]
    ValueTask<int> DescribeNumberAsync(int value);
    [NonCancellable]
    ValueTask<int> FailAsync();
    [NonCancellable]
    ValueTask<int> FailMappedAsync();
    [NonCancellable]
    ValueTask<int> CountInvocationAsync();
    [NonCancellable]
    ValueTask<int> DelayedAsync();
    [NonCancellable]
    ValueTask<string> RequiredNullAsync();
    [NonCancellable]
    ValueTask<string?> OptionalNullAsync();
    [NonCancellable]
    IAsyncEnumerable<string> RequiredNullStreamAsync();
    [NonCancellable]
    IAsyncEnumerable<string?> OptionalNullStreamAsync();
    ValueTask<int> CountOptionalStreamAsync(
        IAsyncEnumerable<string?> values,
        CancellationToken cancellationToken);
    [Oneway]
    [NonCancellable]
    ValueTask NotifyAsync(int value);
    ValueTask<int> SumStreamAsync(IAsyncEnumerable<int> values, CancellationToken cancellationToken);
    ValueTask<int> FailClientStreamAsync(IAsyncEnumerable<int> values, CancellationToken cancellationToken);
    [NonCancellable]
    IAsyncEnumerable<int> FailStreamAsync();
    [NonCancellable]
    IAsyncEnumerable<int> FailMappedStreamAsync();
    [SharpLink.Sdk.Timeout(0.15)]
    IAsyncEnumerable<int> WaitStreamAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<int> FailDuplexAsync(
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken);
}

[RpcService]
public sealed class InterceptorTestService : IInterceptorTestService
{
    private static int _invocationCount;
    private static TaskCompletionSource<bool> s_delayedCallStarted = CreateGate();
    private static TaskCompletionSource<bool> s_delayedCallRelease = CreateGate();

    public static int InvocationCount => Volatile.Read(ref _invocationCount);

    public static void ResetInvocationCount() => Volatile.Write(ref _invocationCount, 0);

    public static Task DelayedCallStarted => Volatile.Read(ref s_delayedCallStarted).Task;

    public static void ResetDelayedCall()
    {
        Volatile.Write(ref s_delayedCallStarted, CreateGate());
        Volatile.Write(ref s_delayedCallRelease, CreateGate());
    }

    public static void ReleaseDelayedCall() => Volatile.Read(ref s_delayedCallRelease).TrySetResult(true);

    public ValueTask<string> DescribeAsync(int value, SharpLinkCallOptions options)
    {
        var source = options.Metadata is { Count: > 0 } metadata
            ? metadata[0].Value
            : "missing";
        var context = SharpLinkCallContext.Current;
        return ValueTask.FromResult($"{value}|{source}|{context?.SessionId}");
    }

    public ValueTask<int> DescribeNumberAsync(int value) => ValueTask.FromResult(value + 1);

    public ValueTask<int> FailAsync()
        => throw new InvalidOperationException("secret-service-detail");

    public ValueTask<int> FailMappedAsync()
        => throw new SharpLinkException(
            SharpLinkErrorCode.ResourceExhausted,
            "public-structured");

    public ValueTask<int> CountInvocationAsync()
        => ValueTask.FromResult(Interlocked.Increment(ref _invocationCount));

    public async ValueTask<int> DelayedAsync()
    {
        Volatile.Read(ref s_delayedCallStarted).TrySetResult(true);
        await Volatile.Read(ref s_delayedCallRelease).Task.ConfigureAwait(false);
        return 42;
    }

    public ValueTask<string> RequiredNullAsync() => ValueTask.FromResult<string>(null!);

    public ValueTask<string?> OptionalNullAsync() => ValueTask.FromResult<string?>(null);

    public async IAsyncEnumerable<string> RequiredNullStreamAsync()
    {
        await Task.Yield();
        yield return null!;
    }

    public async IAsyncEnumerable<string?> OptionalNullStreamAsync()
    {
        await Task.Yield();
        yield return null;
    }

    public async ValueTask<int> CountOptionalStreamAsync(
        IAsyncEnumerable<string?> values,
        CancellationToken cancellationToken)
    {
        var count = 0;
        await foreach (var value in values.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (value is null)
                count++;
        }
        return count;
    }

    public ValueTask NotifyAsync(int value) => ValueTask.CompletedTask;

    public async ValueTask<int> SumStreamAsync(
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken)
    {
        var sum = 0;
        await foreach (var value in values.WithCancellation(cancellationToken).ConfigureAwait(false))
            sum += value;
        return sum;
    }

    public async ValueTask<int> FailClientStreamAsync(
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken)
    {
        await foreach (var _ in values.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
        }
        throw new InvalidOperationException("secret-service-detail");
    }

    public async IAsyncEnumerable<int> FailStreamAsync()
    {
        yield return 1;
        await Task.Yield();
        throw new InvalidOperationException("secret-service-detail");
    }

    public async IAsyncEnumerable<int> FailMappedStreamAsync()
    {
        yield return 1;
        await Task.Yield();
        throw new SharpLinkException(
            SharpLinkErrorCode.ResourceExhausted,
            "public-structured");
    }

    public async IAsyncEnumerable<int> WaitStreamAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 1;
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<int> FailDuplexAsync(
        IAsyncEnumerable<int> values,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var value in values.WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return value;
        throw new InvalidOperationException("secret-service-detail");
    }

    private static TaskCompletionSource<bool> CreateGate()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
