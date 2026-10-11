using System.Collections.Generic;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientContractReceiptTests : ContractAdmissionTestSupport
{
    [Test]
    public async Task SameHashOldRegistrationMustNotSatisfyNewExactReceipt()
    {
        var transport = new TestClientTransportFactory();
        await using var client = Builder().UseTransport(transport).Build();
        await client.ConnectAsync();
        var receipt = Receipt();
        await PublishReceipt(transport.Connection, receipt with { RegistrationId = Guid.NewGuid() });
        var requirement = new ContractRequirement(receipt, ContractRegistrationMatch.ExactRegistration);
        var proxy = (OrdersProxy)client.CreateProxy<IOrdersContract>(new RpcProxyOptions
        { Requirement = requirement, Availability = RpcAvailability.WaitForCompatible });
        var codec = new CountingRequestCodec();
        var call = Invoke(proxy, codec);
        Ensure(!call.IsCompleted && ((SharpLinkClient)client).ContractWaiterCountForTesting == 1 &&
               codec.SerializeCount == 0 && transport.Connection.SentRequestCount == 0,
            "a different same-hash registration at the required generation must remain waiting before serialization");
        Ensure(client.GetContractReadiness(requirement).CompatibleEndpoints == 0, "same hash alone cannot satisfy exact registration");
        await PublishReceipt(transport.Connection, receipt with { Generation = receipt.Generation + 1 }, synchronize: false);
        var request = await transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Request).WaitAsync(RaceCoordinationTimeout);
        Ensure(request.Payload[32] == 1 && request.Payload[33] == (byte)ContractRegistrationMatch.ExactRegistration,
            "the request must carry the exact registration match mode");
        await transport.Connection.InjectInt32ResponseAsync(unchecked((long)request.Header.RequestId), 7);
        Ensure(await call.WaitAsync(RaceCoordinationTimeout) == 7 && codec.SerializeCount == 1, "the same logical call resumes once");
        Ensure(((SharpLinkClient)client).ContractWaiterCountForTesting == 0, "exact admission removes its waiter");
    }

    [Test]
    [Arguments("generation")]
    [Arguments("server")]
    [Arguments("epoch")]
    public async Task MinimumVersionMustCompareOnlyWithinReceiptTimeline(string mismatch)
    {
        var transport = new TestClientTransportFactory();
        await using var client = Builder().UseTransport(transport).Build();
        await client.ConnectAsync();
        var receipt = Receipt();
        var observed = mismatch switch
        {
            "server" => receipt with { ServerInstanceId = Guid.NewGuid(), Generation = 500 },
            "epoch" => receipt with { RegistryEpoch = Guid.NewGuid(), Generation = 500 },
            _ => receipt with { Generation = receipt.Generation - 1 }
        };
        await PublishReceipt(transport.Connection, observed);
        var requirement = new ContractRequirement(receipt, ContractRegistrationMatch.MinimumVersion);
        var proxy = (OrdersProxy)client.CreateProxy<IOrdersContract>(new RpcProxyOptions { Requirement = requirement });
        var codec = new CountingRequestCodec();
        var failure = await CaptureExceptionAsync(Invoke(proxy, codec).WaitAsync(RaceCoordinationTimeout));
        Ensure(failure is SharpLinkContractAdmissionException { Code: SharpLinkErrorCode.Unavailable },
            "lower generations and incomparable server/epoch timelines must reject");
        Ensure(client.GetContractReadiness(requirement).CompatibleEndpoints == 0 && codec.SerializeCount == 0 && transport.Connection.SentRequestCount == 0,
            "incomparable observations must not serialize or send business work");
    }

    [Test]
    public async Task MinimumVersionShouldAcceptLaterSameTimelineRegistration()
    {
        var transport = new TestClientTransportFactory();
        await using var client = Builder().UseTransport(transport).Build();
        await client.ConnectAsync();
        var receipt = Receipt();
        await PublishReceipt(transport.Connection, receipt with { Generation = receipt.Generation + 1, RegistrationId = Guid.NewGuid() });
        var requirement = new ContractRequirement(receipt, ContractRegistrationMatch.MinimumVersion);
        Ensure(client.GetContractReadiness(requirement).CompatibleEndpoints == 1, "later same-timeline registration satisfies minimum");
        var proxy = (OrdersProxy)client.CreateProxy<IOrdersContract>(new RpcProxyOptions { Requirement = requirement });
        await CompleteReceiptCall(proxy, new CountingRequestCodec(), transport.Connection);
    }

    [Test]
    public async Task ReceiptRequirementShouldFilterIndependentEndpointTimelines()
    {
        var intended = new TestClientTransportFactory();
        var unrelated = new TestClientTransportFactory();
        await using var client = Builder().UseEndpoints([Endpoint("intended", 7501), Endpoint("unrelated", 7502)],
            endpoint => endpoint.Id == "intended" ? intended : unrelated).Build();
        await client.WaitForReadinessAsync(2).AsTask().WaitAsync(RaceCoordinationTimeout);
        var receipt = Receipt();
        await PublishReceipt(intended.Connection, receipt);
        await PublishReceipt(unrelated.Connection, receipt with { ServerInstanceId = Guid.NewGuid(), Generation = 50_000 });
        var requirement = new ContractRequirement(receipt, ContractRegistrationMatch.MinimumVersion);
        var proxy = (OrdersProxy)client.CreateProxy<IOrdersContract>(new RpcProxyOptions { Requirement = requirement });
        var codec = new CountingRequestCodec();
        for (var index = 0; index < 4; index++)
            await CompleteReceiptCall(proxy, codec, intended.Connection);
        Ensure(client.GetContractReadiness(requirement).CompatibleEndpoints == 1 &&
               intended.Connection.SentRequestCount == 4 && unrelated.Connection.SentRequestCount == 0,
            "a large generation from another server must not outrank the receipt's timeline");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ProxyOptionsMustRejectRequirementOutsideLocalIdentity(bool wrongContract)
    {
        var transport = new TestClientTransportFactory();
        await using var client = Builder().UseTransport(transport).Build();
        var requirement = new ContractRequirement(wrongContract ? OrdersContractId + 1 : OrdersContractId,
            wrongContract ? Manifest.Instance.RpcAssemblyHash : new RpcHash128(1, 2));
        Exception? failure = null;
        try { _ = client.CreateProxy<IOrdersContract>(new RpcProxyOptions { Requirement = requirement }); }
        catch (Exception exception) { failure = exception; }
        Ensure(failure is ArgumentException && transport.ConnectCount == 0 && transport.Connection.SentRequestCount == 0,
            "local identity mismatch must reject synchronously without remote work");
    }

    private static ContractRegistrationReceipt Receipt()
        => new(Guid.NewGuid(), Guid.NewGuid(), 2, OrdersContractId, Manifest.Instance.RpcAssemblyHash, Guid.NewGuid());

    private static async Task CompleteReceiptCall(OrdersProxy proxy, CountingRequestCodec codec, TestTransportConnection connection)
    {
        var call = Invoke(proxy, codec);
        var request = await connection.WaitForSentFrame(ProtocolV2FrameType.Request).WaitAsync(RaceCoordinationTimeout);
        await connection.InjectInt32ResponseAsync(unchecked((long)request.Header.RequestId), 7);
        Ensure(await call.WaitAsync(RaceCoordinationTimeout) == 7, "receipt-admitted call completes");
    }

    private static async Task PublishReceipt(TestTransportConnection connection,
        ContractRegistrationReceipt receipt, bool synchronize = true)
    {
        using var payload = new PooledByteBufferWriter();
        ProtocolV2ContractManifestCodec.Write(payload,
            new ProtocolV2ContractManifest(receipt.ServerInstanceId, receipt.RegistryEpoch, receipt.Generation,
                [new KeyValuePair<long, RpcHash128>(receipt.ContractId, receipt.RpcAssemblyHash)],
                [new KeyValuePair<long, Guid>(receipt.ContractId, receipt.RegistrationId)]), new SharpLinkProtocolOptions());
        await connection.InjectFrameAsync(ProtocolV2FrameType.ContractManifest, ProtocolV2FrameFlags.None, 0, payload.WrittenMemory);
        if (!synchronize) return;
        await connection.InjectFrameAsync(ProtocolV2FrameType.Ping, ProtocolV2FrameFlags.None, 0, new byte[sizeof(long)]);
        await connection.WaitForSentFrame(ProtocolV2FrameType.Pong).WaitAsync(RaceCoordinationTimeout);
    }
}
