namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private sealed partial class DtoAnalysisState
    {
        private void CollectAdapterRegistrations()
        {
            var assemblies = new Dictionary<string, IAssemblySymbol>(StringComparer.Ordinal)
            {
                [_compilation.Assembly.Identity.ToString()] = _compilation.Assembly
            };
            var pending = new Queue<IAssemblySymbol>();
            pending.Enqueue(_compilation.Assembly);
            while (pending.Count != 0)
            {
                var assembly = pending.Dequeue();
                foreach (var referenced in assembly.Modules.SelectMany(static module => module.ReferencedAssemblySymbols)
                             .OrderBy(static item => item.Identity.ToString(), StringComparer.Ordinal))
                {
                    if (!assemblies.ContainsKey(referenced.Identity.ToString()))
                    {
                        assemblies.Add(referenced.Identity.ToString(), referenced);
                        pending.Enqueue(referenced);
                    }
                }
            }

            var adapterIds = new Dictionary<string, AdapterRegistration>(StringComparer.Ordinal);
            foreach (var assembly in assemblies.Values.OrderBy(static item => item.Identity.ToString(), StringComparer.Ordinal))
            {
                foreach (var attribute in assembly.GetAttributes()
                             .Where(static attribute => IsAttribute(attribute, "SharpLink.Sdk", "RpcCodecAdapterRegistrationAttribute"))
                             .OrderBy(static attribute => attribute.ToString(), StringComparer.Ordinal))
                {
                    var location = attribute.ApplicationSyntaxReference?.GetSyntax(_cancellationToken).GetLocation() ?? Location.None;
                    if (attribute.ConstructorArguments.Length != 2 ||
                        attribute.ConstructorArguments[0].Value is not INamedTypeSymbol adapterType ||
                        attribute.ConstructorArguments[1].Value is not string adapterId ||
                        !IsStableIdentity(adapterId))
                    {
                        Report(DtoDiagnosticKind.AdapterRegistrationInvalid, assembly,
                            "registration requires a concrete Adapter type and non-empty stable ASCII AdapterId", location);
                        continue;
                    }

                    ITypeSymbol? selector = null;
                    foreach (var namedArgument in attribute.NamedArguments)
                    {
                        if (namedArgument.Key == "SelectorAttributeType")
                            selector = namedArgument.Value.Value as ITypeSymbol;
                    }
                    if (!IsValidAdapterType(adapterType))
                    {
                        Report(DtoDiagnosticKind.AdapterTypeInvalid, adapterType,
                            "Adapter must implement IRpcCodecAdapter, be public sealed, and expose a public parameterless constructor", location);
                        continue;
                    }
                    if (!HasValidOpaqueSemanticIdentity(adapterType))
                    {
                        Report(DtoDiagnosticKind.AdapterRegistrationInvalid, adapterType,
                            "Adapter must declare a non-zero fixed semantic identity via [RpcCodecSemanticIdentity(high, low)]", location);
                        continue;
                    }
                    if (selector is not null && !InheritsFromAttribute(selector))
                    {
                        Report(DtoDiagnosticKind.AdapterRegistrationInvalid, selector,
                            "SelectorAttributeType must derive from System.Attribute", location);
                        continue;
                    }

                    var registration = new AdapterRegistration(
                        adapterType,
                        adapterId,
                        selector,
                        location);
                    if (_adaptersByType.TryGetValue(adapterType, out var existingType) &&
                        !string.Equals(existingType.AdapterId, adapterId, StringComparison.Ordinal))
                    {
                        Report(DtoDiagnosticKind.AdapterIdentityConflict, adapterType,
                            "the same Adapter type has inconsistent Adapter IDs", location);
                        continue;
                    }
                    if (adapterIds.TryGetValue(adapterId, out var existingId) &&
                        !SymbolEqualityComparer.Default.Equals(existingId.AdapterType, adapterType))
                    {
                        Report(DtoDiagnosticKind.AdapterIdentityConflict, adapterType,
                            $"Adapter ID '{adapterId}' is declared by inconsistent implementation types", location);
                        continue;
                    }
                    if (selector is not null && _adaptersBySelector.TryGetValue(selector, out var existingSelector) &&
                        !SymbolEqualityComparer.Default.Equals(existingSelector.AdapterType, adapterType))
                    {
                        Report(DtoDiagnosticKind.SelectorConflict, selector,
                            "one selector Attribute cannot select multiple Codec Adapters", location);
                        continue;
                    }

                    _adaptersByType[adapterType] = registration;
                    adapterIds[adapterId] = registration;
                    if (selector is not null)
                        _adaptersBySelector[selector] = registration;
                }
            }
        }

        private bool TrySelectAdapter(ITypeSymbol type, out AdapterRegistration? selected)
        {
            if (!TryCollectExplicitAdapterCandidates(type, reportInvalid: true, out var candidates))
            {
                selected = null;
                _failed.Add(GetTypeName(type));
                return true;
            }
            if (candidates.Count == 0)
            {
                selected = null;
                return false;
            }

            var resolved = new List<AdapterRegistration>();
            foreach (var candidate in candidates)
            {
                if (!TryResolveExplicitBinding(type, candidate, reportInvalid: true, out var registration))
                {
                    selected = null;
                    _failed.Add(GetTypeName(type));
                    return true;
                }
                if (registration is not null && !resolved.Any(existing => AdapterRegistrationsEqual(existing, registration)))
                    resolved.Add(registration);
            }

            if (resolved.Count != 1)
            {
                Report(DtoDiagnosticKind.AdapterSelectionConflict, type,
                    "the target selects multiple different explicit Codec Adapters", candidates[0].Location);
                selected = null;
                _failed.Add(GetTypeName(type));
                return true;
            }
            selected = resolved[0];
            return true;
        }

        private bool HasResolvableExplicitAdapter(ITypeSymbol type)
        {
            if (!TryCollectExplicitAdapterCandidates(type, reportInvalid: false, out var candidates) ||
                candidates.Count == 0)
            {
                return false;
            }

            var resolved = new List<AdapterRegistration>();
            foreach (var candidate in candidates)
            {
                if (!TryResolveExplicitBinding(type, candidate, reportInvalid: false, out var registration) ||
                    registration is null)
                {
                    return false;
                }
                if (!resolved.Any(existing => AdapterRegistrationsEqual(existing, registration)))
                    resolved.Add(registration);
            }
            return resolved.Count == 1;
        }

        private bool TryCollectExplicitAdapterCandidates(
            ITypeSymbol type,
            bool reportInvalid,
            out List<ExplicitBindingCandidate> candidates)
        {
            candidates = [];
            foreach (var attribute in type.GetAttributes())
            {
                var location = attribute.ApplicationSyntaxReference?.GetSyntax(_cancellationToken).GetLocation() ??
                    type.Locations.FirstOrDefault() ?? Location.None;
                if (IsAttribute(attribute, "SharpLink.Sdk", "RpcCodecAdapterAttribute"))
                {
                    if (attribute.ConstructorArguments.Length != 1 ||
                        attribute.ConstructorArguments[0].Value is not INamedTypeSymbol adapter)
                    {
                        if (reportInvalid)
                        {
                            Report(DtoDiagnosticKind.AdapterBindingInvalid, type,
                                "type-level RpcCodecAdapter requires only adapterType", location);
                        }
                        return false;
                    }
                    if (_contractMode && !_selectorOnlyContractDefaults)
                        _contractOwnedPolicyRoots.Add(GetCanonicalPolicyTargetIdentity(type));
                    candidates.Add(new ExplicitBindingCandidate(adapter, location));
                }
                if (attribute.AttributeClass is { } attributeClass &&
                    _adaptersBySelector.TryGetValue(attributeClass, out var selectorRegistration))
                {
                    candidates.Add(new ExplicitBindingCandidate(selectorRegistration.AdapterType, location));
                }
            }
            if (_assemblyBindings.TryGetValue(NormalizeAdapterTarget(type), out var assemblyBinding))
                candidates.Add(assemblyBinding);
            return true;
        }

        private bool TryResolveExplicitBinding(
            ITypeSymbol target,
            ExplicitBindingCandidate candidate,
            bool reportInvalid,
            out AdapterRegistration? selected)
        {
            if (_adaptersByType.TryGetValue(candidate.ImplementationType, out var adapter))
            {
                selected = adapter;
                return true;
            }

            if (reportInvalid)
            {
                Report(
                    DtoDiagnosticKind.AdapterRegistrationInvalid,
                    target,
                    $"selected Adapter '{GetTypeName(candidate.ImplementationType)}' has no valid RpcCodecAdapterRegistration",
                    candidate.Location);
            }
            selected = null;
            return false;
        }

        private static bool AdapterRegistrationsEqual(AdapterRegistration left, AdapterRegistration right)
            => SymbolEqualityComparer.Default.Equals(left.AdapterType, right.AdapterType) &&
               string.Equals(left.AdapterId, right.AdapterId, StringComparison.Ordinal);

        private static bool ImplementsRpcCodecAdapter(INamedTypeSymbol type)
            => type.AllInterfaces.Any(static item =>
                item.Name == "IRpcCodecAdapter" &&
                item.ContainingNamespace.ToDisplayString() == "SharpLink.Abstractions");

        private static bool IsValidAdapterType(INamedTypeSymbol type)
            => IsEffectivelyPublic(type) &&
               type.IsSealed &&
               type.InstanceConstructors.Any(static constructor =>
                   constructor.DeclaredAccessibility == Accessibility.Public &&
                   constructor.Parameters.Length == 0) &&
               ImplementsRpcCodecAdapter(type);

        private bool HasResolvableCustomCodec(ITypeSymbol type)
        {
            var candidates = new List<ITypeSymbol>();
            foreach (var attribute in type.GetAttributes())
            {
                if (!IsAttribute(attribute, "SharpLink.Sdk", "RpcCodecAttribute"))
                    continue;
                if (attribute.ConstructorArguments.Length != 1 ||
                    attribute.ConstructorArguments[0].Value is not ITypeSymbol codec)
                {
                    return false;
                }
                candidates.Add(codec);
            }

            if (_customCodecBindings.TryGetValue(NormalizeAdapterTarget(type), out var assemblyBinding))
                candidates.Add(assemblyBinding.CodecType);
            if (candidates.Count == 0)
                return false;

            ITypeSymbol? selected = null;
            foreach (var candidate in candidates)
            {
                if (selected is null)
                {
                    selected = candidate;
                    continue;
                }
                if (!SymbolEqualityComparer.Default.Equals(selected, candidate))
                    return false;
            }

            return selected is not null && IsValidCustomCodec(selected, type);
        }

        private static bool IsValidCustomCodec(ITypeSymbol codecType, ITypeSymbol targetType)
        {
            if (codecType is not INamedTypeSymbol named ||
                HasTypeParameter(named) ||
                !IsEffectivelyPublic(named) ||
                !named.IsSealed ||
                !named.InstanceConstructors.Any(static constructor =>
                    constructor.DeclaredAccessibility == Accessibility.Public &&
                    constructor.Parameters.Length == 0))
            {
                return false;
            }

            if (!named.AllInterfaces.Any(item =>
                    item.Name == "IRpcCodec" &&
                    item.ContainingNamespace.ToDisplayString() == "SharpLink.Abstractions" &&
                    item is INamedTypeSymbol { IsGenericType: true } generic &&
                    generic.TypeArguments.Length == 1 &&
                    SymbolEqualityComparer.Default.Equals(generic.TypeArguments[0], targetType)))
            {
                return false;
            }

            return HasValidOpaqueSemanticIdentity(named);
        }

        private CustomCodecRegistration? ValidateCustomCodec(
            ITypeSymbol codecType,
            ITypeSymbol targetType,
            Location location)
        {
            if (codecType is not INamedTypeSymbol named)
            {
                Report(DtoDiagnosticKind.CustomCodecTypeInvalid, codecType,
                    "custom Codec must be a closed, public sealed type", location);
                return null;
            }

            if (HasTypeParameter(named) ||
                !IsEffectivelyPublic(named) ||
                !named.IsSealed ||
                !named.InstanceConstructors.Any(static constructor =>
                    constructor.DeclaredAccessibility == Accessibility.Public &&
                    constructor.Parameters.Length == 0))
            {
                Report(DtoDiagnosticKind.CustomCodecTypeInvalid, codecType,
                    "custom Codec must be a public sealed type with a public parameterless constructor", location);
                return null;
            }

            var implementsTargetCodec = named.AllInterfaces.Any(item =>
                item.Name == "IRpcCodec" &&
                item.ContainingNamespace.ToDisplayString() == "SharpLink.Abstractions" &&
                item is INamedTypeSymbol { IsGenericType: true } generic &&
                generic.TypeArguments.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(generic.TypeArguments[0], targetType));
            if (!implementsTargetCodec)
            {
                Report(DtoDiagnosticKind.CustomCodecTypeInvalid, codecType,
                    $"custom Codec must implement IRpcCodec<{GetTypeName(targetType)}>", location);
                return null;
            }

            if (!HasValidOpaqueSemanticIdentity(named))
            {
                Report(DtoDiagnosticKind.CustomCodecIdentityInvalid, codecType,
                    "custom Codec must declare a non-zero fixed semantic identity via [RpcCodecSemanticIdentity(high, low)]", location);
                return null;
            }

            return new CustomCodecRegistration(named, location);
        }

        private bool TrySelectCustomCodec(ITypeSymbol type, out CustomCodecRegistration? selected)
        {
            var candidates = new List<(ITypeSymbol Codec, Location Location)>();
            foreach (var attribute in type.GetAttributes())
            {
                if (!IsAttribute(attribute, "SharpLink.Sdk", "RpcCodecAttribute"))
                    continue;

                var location = attribute.ApplicationSyntaxReference?.GetSyntax(_cancellationToken).GetLocation() ??
                    type.Locations.FirstOrDefault() ?? Location.None;
                if (attribute.ConstructorArguments.Length != 1 ||
                    attribute.ConstructorArguments[0].Value is not ITypeSymbol codec)
                {
                    Report(DtoDiagnosticKind.CustomCodecBindingInvalid, type,
                        "type-level RpcCodec requires only codecType", location);
                    selected = null;
                    return true;
                }
                candidates.Add((codec, location));
            }

            if (_customCodecBindings.TryGetValue(NormalizeAdapterTarget(type), out var assemblyBinding))
                candidates.Add((assemblyBinding.CodecType, assemblyBinding.Location));

            if (candidates.Count == 0)
            {
                selected = null;
                return false;
            }

            var distinct = new List<ITypeSymbol>();
            foreach (var candidate in candidates)
            {
                if (!distinct.Any(existing => SymbolEqualityComparer.Default.Equals(existing, candidate.Codec)))
                    distinct.Add(candidate.Codec);
            }
            if (distinct.Count != 1)
            {
                Report(DtoDiagnosticKind.CustomCodecSelectionConflict, type,
                    "the target selects multiple different custom Codec implementations", candidates[0].Location);
                selected = null;
                _failed.Add(GetTypeName(type));
                return true;
            }

            selected = ValidateCustomCodec(distinct[0], type, candidates[0].Location);
            if (selected is null)
                _failed.Add(GetTypeName(type));
            return true;
        }

        private static bool IsEffectivelyPublic(INamedTypeSymbol type)
        {
            for (var current = type; current is not null; current = current.ContainingType)
            {
                if (current.DeclaredAccessibility != Accessibility.Public)
                    return false;
            }
            return true;
        }

        private static bool InheritsFromAttribute(ITypeSymbol type)
        {
            for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
            {
                if (current.Name == "Attribute" && current.ContainingNamespace.ToDisplayString() == "System")
                    return true;
            }
            return false;
        }

        private static bool HasValidOpaqueSemanticIdentity(INamedTypeSymbol type)
        {
            var identity = type.GetAttributes().FirstOrDefault(static attribute =>
                IsAttribute(attribute, "SharpLink.Sdk", "RpcCodecSemanticIdentityAttribute"));
            return identity is not null &&
                   identity.ConstructorArguments.Length == 2 &&
                   identity.ConstructorArguments[0].Value is ulong high &&
                   identity.ConstructorArguments[1].Value is ulong low &&
                   (high | low) != 0;
        }

        private static bool IsStableIdentity(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;
            foreach (var character in value)
            {
                if (character < 0x21 || character > 0x7E)
                    return false;
            }
            return true;
        }

        private sealed record ExplicitBindingCandidate(
            INamedTypeSymbol ImplementationType,
            Location Location);

        private sealed record AdapterRegistration(
            INamedTypeSymbol AdapterType,
            string AdapterId,
            ITypeSymbol? SelectorType,
            Location Location);

        private sealed record CustomCodecRegistration(
            INamedTypeSymbol CodecType,
            Location Location);
    }
}
