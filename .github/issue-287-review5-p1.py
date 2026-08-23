from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text()
    count = text.count(old)
    assert count == 1, f"{path}: expected one match, found {count}"
    p.write_text(text.replace(old, new, 1))


# P1: nullable flow after a successful terminal claim.
replace_once(
    "src/SharpLink.Client/PendingRequestTable.cs",
    "        CompleteTakenCall(call, reason, exception: null, ref payload);",
    "        CompleteTakenCall(call!, reason, exception: null, ref payload);")

# P1: tracked timed requests must observe an emission-time drop independently of the deadline timer.
replace_once(
    "src/SharpLink.Client/SharpLinkClient.Invokers.cs",
    """                SendRpcCall(
                    connection.Session,
                    contractId,
                    methodId,
                    requestId,
                    flags,
                    request,
                    requestCodec,
                    control.Deadline,
                    control.Metadata);""",
    """                var emission = SendRpcCall(
                    connection.Session,
                    contractId,
                    methodId,
                    requestId,
                    flags,
                    request,
                    requestCodec,
                    control.Deadline,
                    control.Metadata,
                    observeEmission: control.Deadline.HasValue,
                    cancellationToken: CancellationToken.None);
                if (!emission.IsCompletedSuccessfully)
                {
                    TrackFrameworkTask(
                        ObserveTrackedRequestEmissionAsync(connection, requestId, emission),
                        \"UnaryRequestEmission\");
                }""")

replace_once(
    "src/SharpLink.Client/SharpLinkClient.Invokers.cs",
    """            await SendRpcCall(
                connection.Session,
                method.ContractId,
                method.MethodId,
                requestId,
                cancellationToken.CanBeCanceled || control.Deadline.HasValue
                    ? ProtocolV2FrameFlags.Cancellable
                    : ProtocolV2FrameFlags.None,
                request,
                requestCodec,
                control.Deadline,
                control.Metadata);""",
    """            await SendRpcCall(
                connection.Session,
                method.ContractId,
                method.MethodId,
                requestId,
                cancellationToken.CanBeCanceled || control.Deadline.HasValue
                    ? ProtocolV2FrameFlags.Cancellable
                    : ProtocolV2FrameFlags.None,
                request,
                requestCodec,
                control.Deadline,
                control.Metadata,
                observeEmission: control.Deadline.HasValue,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);""")

replace_once(
    "src/SharpLink.Client/SharpLinkClient.Invokers.cs",
    """    private ValueTask<StreamCallRegistration> PrepareGeneratedServerStreamAsync<TResponse>(""",
    """    private async Task ObserveTrackedRequestEmissionAsync(
        ClientConnection connection,
        long requestId,
        ValueTask emission)
    {
        try
        {
            await emission.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var deadlineExceeded = exception is SharpLinkException
                { Code: SharpLinkErrorCode.DeadlineExceeded };
            connection.PendingCalls.TryComplete(
                requestId,
                deadlineExceeded
                    ? PendingCallCompletionReason.DeadlineExceeded
                    : PendingCallCompletionReason.SendFailure,
                deadlineExceeded ? null : exception);
        }
    }

    private ValueTask<StreamCallRegistration> PrepareGeneratedServerStreamAsync<TResponse>(""")

# P1: an untracked OneWay must not return caller cancellation while its accepted frame can still emit.
replace_once(
    "src/SharpLink.Client/SharpLinkClient.Invokers.cs",
    """        if (!method.HasClientStreams && !connection.TryBeginUntrackedCall())
        {
            var exception = new SharpLinkException(""",
    """        if (!method.HasClientStreams)
            cancellationToken.ThrowIfCancellationRequested();
        if (!method.HasClientStreams && !connection.TryBeginUntrackedCall())
        {
            var exception = new SharpLinkException(""")

replace_once(
    "src/SharpLink.Client/SharpLinkClient.Invokers.cs",
    """                    observeEmission: control.Deadline.HasValue,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (method.HasClientStreams)""",
    """                    observeEmission: control.Deadline.HasValue,
                    cancellationToken: method.HasClientStreams
                        ? cancellationToken
                        : CancellationToken.None).ConfigureAwait(false);
                if (method.HasClientStreams)""")

# P1: API4 is only valid with the exact current ABI locator on catalog/static validation.
replace_once(
    "src/SharpLink.Runtime/SharpLinkGeneratedManifestCompatibility.cs",
    """        var owner = expectedOwner ?? manifest.OwnerAssembly;
        if (owner is not null)
        {
            foreach (var attribute in owner.GetCustomAttributesData())
            {
                if (!string.Equals(
                        attribute.AttributeType.FullName,
                        typeof(SharpLinkGeneratedAssemblyManifestAttribute).FullName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var actualIdentity = attribute.ConstructorArguments.Count >= 5
                    ? attribute.ConstructorArguments[4].Value as string
                    : null;
                if (!string.Equals(
                        actualIdentity,
                        SharpLinkGeneratedManifestVersions.AbiIdentity,
                        StringComparison.Ordinal))
                {
                    return Error(
                        SharpLinkAssemblyRegistrationErrorCode.IncompatibleManifest,
                        FormatVersionMismatch(
                            apiVersion,
                            protocolVersion,
                            TryGetGeneratorVersion(manifest),
                            actualIdentity ?? \"<missing: pre-current ABI locator>\"),
                        owner,
                        \"Manifest\");
                }
                break;
            }
        }

        return null;""",
    """        var owner = expectedOwner ?? manifest.OwnerAssembly;
        if (owner is not null)
        {
            var locatorFound = false;
            foreach (var attribute in owner.GetCustomAttributesData())
            {
                if (!string.Equals(
                        attribute.AttributeType.FullName,
                        typeof(SharpLinkGeneratedAssemblyManifestAttribute).FullName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                locatorFound = true;
                var actualIdentity = attribute.ConstructorArguments.Count >= 5
                    ? attribute.ConstructorArguments[4].Value as string
                    : null;
                if (!string.Equals(
                        actualIdentity,
                        SharpLinkGeneratedManifestVersions.AbiIdentity,
                        StringComparison.Ordinal))
                {
                    return Error(
                        SharpLinkAssemblyRegistrationErrorCode.IncompatibleManifest,
                        FormatVersionMismatch(
                            apiVersion,
                            protocolVersion,
                            TryGetGeneratorVersion(manifest),
                            actualIdentity ?? \"<missing: pre-current ABI locator>\"),
                        owner,
                        \"Manifest\");
                }
                break;
            }

            // Loader validation supplies expectedOwner only after it has already parsed and
            // validated the locator. Catalog/static registration has no such preflight, so the
            // exact ABI identity must be present here rather than falling back to API integer 4.
            if (expectedOwner is null && !locatorFound)
            {
                return Error(
                    SharpLinkAssemblyRegistrationErrorCode.IncompatibleManifest,
                    FormatVersionMismatch(
                        apiVersion,
                        protocolVersion,
                        TryGetGeneratorVersion(manifest),
                        \"<missing: no current ABI locator>\"),
                    owner,
                    \"Manifest\");
            }
        }

        return null;""")

# Regression for the public/static validation path that previously false-accepted API4 without a locator.
replace_once(
    "test/SharpLink.UnitTests/Runtime/GeneratedManifestCompatibilityTests.cs",
    """    [Test]
    public void ValidatorShouldValidateShapeBeforeRejectingOwnership()""",
    """    [Test]
    public void CurrentApiWithoutExactLocatorShouldRejectBeforeReadingManifestShape()
    {
        var manifest = new ProbeManifest(
            SharpLinkGeneratedManifestVersions.Api,
            SharpLinkGeneratedManifestVersions.Protocol,
            typeof(GeneratedManifestCompatibilityTests).Assembly);

        var error = SharpLinkGeneratedManifestCompatibility.Validate(manifest);

        Ensure(error?.Code == SharpLinkAssemblyRegistrationErrorCode.IncompatibleManifest,
            \"current API without an exact locator should be incompatible\");
        Ensure(error!.Message.Contains(\"<missing: no current ABI locator>\", StringComparison.Ordinal),
            \"missing-locator diagnostic should identify the exact ABI discriminator\");
        Ensure(manifest.ShapeReads == 0,
            \"missing exact locator must be rejected before manifest shape validation\");
    }

    [Test]
    public void ValidatorShouldValidateShapeBeforeRejectingOwnership()""")

print("review5 P1 source patch applied")
