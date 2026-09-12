using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Reflection;
using SharpLink.Client;
using SharpLink.Server;

namespace SharpLink.IntegrationTests;

internal static class ServerRegistryTestAccessor
{
    private const BindingFlags InstanceFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly FieldInfo ConnectionRegistryField =
        RequireField(typeof(SharpLinkServer), "_connectionRegistry");
    private static readonly FieldInfo ActiveConnectionsField =
        RequireField(ConnectionRegistryField.FieldType, "_active");
    private static readonly FieldInfo RetiredConnectionsField =
        RequireField(ConnectionRegistryField.FieldType, "_retired");
    private static readonly FieldInfo ServiceModuleRegistryField =
        RequireField(typeof(SharpLinkServer), "_serviceModuleRegistry");
    private static readonly FieldInfo ServicesField =
        RequireField(ServiceModuleRegistryField.FieldType, "_services");
    private static readonly FieldInfo DynamicModulesField =
        RequireField(ServiceModuleRegistryField.FieldType, "_dynamicModules");
    private static readonly FieldInfo ClientAssemblyRegistryField =
        RequireField(typeof(SharpLinkClient), "_assemblyRegistry");
    private static readonly MethodInfo PublishServicesMethod =
        ServiceModuleRegistryField.FieldType.GetMethod("PublishServices", InstanceFlags)
        ?? throw new MissingMethodException(ServiceModuleRegistryField.FieldType.FullName, "PublishServices");

    internal static int ActiveConnectionCount(SharpLinkServer server)
        => ActiveConnections(server).Count;

    internal static int RetiredConnectionCount(SharpLinkServer server)
        => ReadCount(RetiredConnectionsField.GetValue(ConnectionRegistry(server)), "retired connections");

    internal static ConcurrentDictionary<string, ServerConnectionState> ActiveConnections(SharpLinkServer server)
        => (ConcurrentDictionary<string, ServerConnectionState>)(
            ActiveConnectionsField.GetValue(ConnectionRegistry(server))
            ?? throw new InvalidOperationException("Server active connection registry was null."));

    internal static FrozenDictionary<long, ServiceRegistration> Services(SharpLinkServer server)
        => (FrozenDictionary<long, ServiceRegistration>)(
            ServicesField.GetValue(ServiceModuleRegistry(server))
            ?? throw new InvalidOperationException("Server service registry was null."));

    internal static int ServiceCount(SharpLinkServer server)
        => Services(server).Count;

    internal static void PublishServices(
        SharpLinkServer server,
        FrozenDictionary<long, ServiceRegistration> services)
        => PublishServicesMethod.Invoke(ServiceModuleRegistry(server), [services]);

    internal static IDictionary DynamicModules(object endpoint)
    {
        if (endpoint is SharpLinkServer server)
        {
            return DynamicModulesField.GetValue(ServiceModuleRegistry(server)) as IDictionary
                   ?? throw new InvalidOperationException("Server dynamic module registry was unavailable.");
        }
        if (endpoint is SharpLinkClient client)
        {
            var registry = ClientAssemblyRegistryField.GetValue(client) as ClientAssemblyRegistry
                ?? throw new InvalidOperationException("Client assembly registry was unavailable.");
            return registry.DynamicModules as IDictionary
                   ?? throw new InvalidOperationException("Client dynamic module registry was unavailable.");
        }

        var field = endpoint.GetType().GetField("_dynamicModules", InstanceFlags)
            ?? throw new MissingFieldException(endpoint.GetType().FullName, "_dynamicModules");
        return field.GetValue(endpoint) as IDictionary
               ?? throw new InvalidOperationException("Dynamic module registry was unavailable.");
    }

    internal static int DynamicModuleCount(object endpoint)
        => DynamicModules(endpoint).Count;

    internal static SharpLinkDynamicModule DynamicModule(object endpoint, Assembly assembly)
        => DynamicModules(endpoint)[assembly] as SharpLinkDynamicModule
           ?? throw new InvalidOperationException(
               $"Dynamic module was not found for '{assembly.FullName}'.");

    private static object ConnectionRegistry(SharpLinkServer server)
        => ConnectionRegistryField.GetValue(server)
           ?? throw new InvalidOperationException("Server connection registry was unavailable.");

    private static object ServiceModuleRegistry(SharpLinkServer server)
        => ServiceModuleRegistryField.GetValue(server)
           ?? throw new InvalidOperationException("Server service/module registry was unavailable.");

    private static FieldInfo RequireField(Type type, string name)
        => type.GetField(name, InstanceFlags)
           ?? throw new MissingFieldException(type.FullName, name);

    private static int ReadCount(object? value, string name)
    {
        if (value is null)
            throw new InvalidOperationException($"{name} registry was null.");
        return (int)(value.GetType().GetProperty("Count", InstanceFlags)?.GetValue(value)
            ?? throw new MissingMemberException(value.GetType().FullName, "Count"));
    }
}
