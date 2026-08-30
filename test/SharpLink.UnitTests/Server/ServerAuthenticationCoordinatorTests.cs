using Microsoft.Extensions.Logging.Abstractions;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public class ServerAuthenticationCoordinatorTests
{
    [Test]
    public async Task MissingProviderShouldRespectRequiredAuthentication()
    {
        var optional = CreateCoordinator(authenticator: null, authenticationRequired: false);
        var required = CreateCoordinator(authenticator: null, authenticationRequired: true);
        var request = CreateRequest();

        var optionalResult = await optional.AuthenticateAsync(request, CancellationToken.None);
        var requiredResult = await required.AuthenticateAsync(request, CancellationToken.None);

        Ensure(optionalResult.IsAuthenticated, "optional authentication should accept without a provider");
        Ensure(optionalResult.Context is null, "optional authentication should not invent an identity");
        Ensure(!requiredResult.IsAuthenticated, "required authentication should reject without a provider");
        Ensure(requiredResult.ErrorCode == SharpLinkErrorCode.AuthenticationRejected, "required rejection code");
    }

    [Test]
    public async Task SuccessfulProviderShouldPreserveEstablishedContext()
    {
        var context = new SharpLinkAuthenticationContext(
            subject: "user-42",
            tenantId: "tenant-a",
            scopes: ["orders.read"],
            expiresAt: DateTimeOffset.MaxValue);
        var coordinator = CreateCoordinator(SharpLinkAuthenticator.CreateServer((request, cancellationToken) =>
            ValueTask.FromResult(SharpLinkAuthenticationResult.Authenticate(context))));

        var result = await coordinator.AuthenticateAsync(CreateRequest(), CancellationToken.None);

        Ensure(result.IsAuthenticated, "provider success should authenticate");
        Ensure(ReferenceEquals(context, result.Context), "established context should be preserved");
        Ensure(result.ErrorCode == SharpLinkErrorCode.Unknown, "successful result should retain Unknown error code");
    }

    [Test]
    public async Task ProviderResultsShouldBeNormalizedAtTheAuthenticationBoundary()
    {
        var contradictory = CreateCoordinator(SharpLinkAuthenticator.CreateServer((request, cancellationToken) =>
            ValueTask.FromResult(new SharpLinkAuthenticationResult(
                IsAuthenticated: true,
                ErrorCode: SharpLinkErrorCode.AuthenticationRejected,
                ErrorMessage: null,
                Context: null))));
        var unknownFailure = CreateCoordinator(SharpLinkAuthenticator.CreateServer((request, cancellationToken) =>
            ValueTask.FromResult(new SharpLinkAuthenticationResult(
                IsAuthenticated: false,
                ErrorCode: SharpLinkErrorCode.Unknown,
                ErrorMessage: "provider rejected",
                Context: null))));
        var undefinedFailure = CreateCoordinator(SharpLinkAuthenticator.CreateServer((request, cancellationToken) =>
            ValueTask.FromResult(new SharpLinkAuthenticationResult(
                IsAuthenticated: false,
                ErrorCode: (SharpLinkErrorCode)int.MaxValue,
                ErrorMessage: "undefined",
                Context: null))));
        var expired = CreateCoordinator(SharpLinkAuthenticator.CreateServer((request, cancellationToken) =>
            ValueTask.FromResult(SharpLinkAuthenticationResult.Authenticate(
                new SharpLinkAuthenticationContext(expiresAt: DateTimeOffset.MinValue)))));

        var contradictoryResult = await contradictory.AuthenticateAsync(CreateRequest(), CancellationToken.None);
        var unknownFailureResult = await unknownFailure.AuthenticateAsync(CreateRequest(), CancellationToken.None);
        var undefinedFailureResult = await undefinedFailure.AuthenticateAsync(CreateRequest(), CancellationToken.None);
        var expiredResult = await expired.AuthenticateAsync(CreateRequest(), CancellationToken.None);

        Ensure(!contradictoryResult.IsAuthenticated, "contradictory success should be rejected");
        Ensure(contradictoryResult.ErrorCode == SharpLinkErrorCode.AuthenticationRejected, "contradictory result code");
        Ensure(!unknownFailureResult.IsAuthenticated, "unknown failure should remain rejected");
        Ensure(unknownFailureResult.ErrorCode == SharpLinkErrorCode.AuthenticationRejected, "unknown failure should normalize");
        Ensure(unknownFailureResult.ErrorMessage == "provider rejected", "provider rejection message should be preserved");
        Ensure(!undefinedFailureResult.IsAuthenticated, "undefined failure should be rejected");
        Ensure(undefinedFailureResult.ErrorCode == SharpLinkErrorCode.AuthenticationRejected, "undefined failure should normalize");
        Ensure(!expiredResult.IsAuthenticated, "expired identity should be rejected");
        Ensure(expiredResult.ErrorCode == SharpLinkErrorCode.AuthenticationExpired, "expired identity rejection code");
    }

    [Test]
    public async Task ProviderExceptionShouldReturnSafeFailureWithoutLeakingProviderDetails()
    {
        var coordinator = CreateCoordinator(SharpLinkAuthenticator.CreateServer((request, cancellationToken) =>
            ValueTask.FromException<SharpLinkAuthenticationResult>(
                new InvalidOperationException("secret-token-value"))));

        var result = await coordinator.AuthenticateAsync(CreateRequest(), CancellationToken.None);

        Ensure(!result.IsAuthenticated, "provider exception should reject authentication");
        Ensure(result.ErrorCode == SharpLinkErrorCode.AuthenticationRejected, "provider exception rejection code");
        Ensure(result.ErrorMessage == "Authentication failed.", "provider details must not reach the peer");
    }

    [Test]
    public async Task CallerCancellationShouldPropagate()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var coordinator = CreateCoordinator(SharpLinkAuthenticator.CreateServer((request, cancellationToken) =>
            ValueTask.FromException<SharpLinkAuthenticationResult>(
                new OperationCanceledException(cancellationToken))));

        try
        {
            await coordinator.AuthenticateAsync(CreateRequest(), cancellation.Token);
            throw new Exception("expected authentication cancellation to propagate");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static ServerAuthenticationCoordinator CreateCoordinator(
        ISharpLinkServerAuthenticator? authenticator,
        bool authenticationRequired = false)
        => new(
            authenticator,
            authenticationRequired,
            NullLogger.Instance,
            TimeProvider.System);

    private static SharpLinkAuthenticationRequest CreateRequest()
        => new(
            "connection-1",
            ReadOnlyMemory<byte>.Empty,
            LocalEndPoint: null,
            RemoteEndPoint: null);

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
