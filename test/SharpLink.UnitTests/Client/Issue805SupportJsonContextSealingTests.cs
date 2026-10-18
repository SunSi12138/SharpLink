using System.Text.Json.Serialization;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

internal static class Issue805SupportJsonContextSealingTests
{
    internal static void Verify()
    {
        foreach (var context in new[]
        {
            typeof(SharpLinkClientSupportIndentedJsonContext),
            typeof(SharpLinkClientSupportCompactJsonContext)
        })
        {
            if (context.IsPublic || !context.IsSealed ||
                !typeof(JsonSerializerContext).IsAssignableFrom(context))
            {
                throw new Exception(
                    $"Support-only generated JSON context {context.Name} must be internal, sealed and typed.");
            }
        }

        if (SharpLinkClientSupportIndentedJsonContext.Default.SharpLinkClientSupportSnapshot is null ||
            SharpLinkClientSupportCompactJsonContext.Default.SharpLinkClientSupportSnapshot is null)
        {
            throw new Exception("Both source-generated support snapshot serializers must still be available.");
        }
    }
}
