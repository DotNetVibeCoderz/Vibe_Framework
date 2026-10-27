using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace JSNet.Runtime;

public enum ServiceScope { Singleton, Scoped, Transient }

/// <summary>
/// <c>@Service</c> decorator: marks a class for automatic registration in the .NET dependency injection
/// container (as itself and every interface it implements).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ServiceAttribute(ServiceScope scope = ServiceScope.Scoped) : Attribute
{
    public ServiceScope Scope { get; } = scope;
}

public static class JsNetServiceCollectionExtensions
{
    /// <summary>Registers every <c>@Service</c> class of <paramref name="assembly"/> (default: the calling assembly).</summary>
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Scans the assembly for @Service classes; register services explicitly in trimmed/AOT apps.")]
    public static IServiceCollection AddJsNetServices(this IServiceCollection services, Assembly? assembly = null)
    {
        assembly ??= Assembly.GetCallingAssembly();
        foreach (var type in assembly.GetTypes())
        {
            if (type.GetCustomAttribute<ServiceAttribute>() is not { } attr || type.IsAbstract) continue;
            var lifetime = attr.Scope switch
            {
                ServiceScope.Singleton => ServiceLifetime.Singleton,
                ServiceScope.Transient => ServiceLifetime.Transient,
                _ => ServiceLifetime.Scoped,
            };
            services.Add(new ServiceDescriptor(type, type, lifetime));
            foreach (var iface in type.GetInterfaces().Where(i => i.Assembly == assembly || i.IsPublic))
                services.Add(new ServiceDescriptor(iface, sp => sp.GetRequiredService(type), lifetime));
        }
        return services;
    }

    /// <summary>Lower-case alias for JS.Net code: <c>builder.Services.addJsNetServices()</c>.</summary>
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Scans the assembly for @Service classes; register services explicitly in trimmed/AOT apps.")]
    public static IServiceCollection addJsNetServices(this IServiceCollection services, Assembly? assembly = null) =>
        services.AddJsNetServices(assembly ?? Assembly.GetCallingAssembly());
}
