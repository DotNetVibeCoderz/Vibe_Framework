using System.Reflection;
using Microsoft.AspNetCore.Components;

namespace Rustzor.Components;

public enum ServiceLifetime
{
    Singleton,
    /// <summary>In the browser there is one scope, the page, so scoped is singleton (as in Blazor WebAssembly).</summary>
    Scoped,
    Transient,
}

public sealed class ServiceDescriptor
{
    public ServiceDescriptor(Type serviceType, Type? implementationType, object? instance, Func<ServiceProvider, object>? factory, ServiceLifetime lifetime)
    {
        ServiceType = serviceType;
        ImplementationType = implementationType;
        Instance = instance;
        Factory = factory;
        Lifetime = lifetime;
    }

    public Type ServiceType { get; }
    public Type? ImplementationType { get; }
    public object? Instance { get; internal set; }
    public Func<ServiceProvider, object>? Factory { get; }
    public ServiceLifetime Lifetime { get; }
}

/// <summary>The services an app registers in Program.cs.</summary>
/// <remarks>
/// Microsoft.Extensions.DependencyInjection is a framework assembly the browser
/// runtime does not carry; this is a small container with the same verbs.
/// </remarks>
public sealed class ServiceCollection
{
    internal readonly List<ServiceDescriptor> Descriptors = new();

    public ServiceCollection Add(ServiceDescriptor descriptor)
    {
        Descriptors.Add(descriptor);
        return this;
    }

    public ServiceCollection AddSingleton<TService>() where TService : class =>
        Add(new ServiceDescriptor(typeof(TService), typeof(TService), null, null, ServiceLifetime.Singleton));

    public ServiceCollection AddSingleton<TService, TImplementation>() where TImplementation : class, TService =>
        Add(new ServiceDescriptor(typeof(TService), typeof(TImplementation), null, null, ServiceLifetime.Singleton));

    public ServiceCollection AddSingleton<TService>(TService instance) where TService : class =>
        Add(new ServiceDescriptor(typeof(TService), null, instance, null, ServiceLifetime.Singleton));

    public ServiceCollection AddSingleton<TService>(Func<ServiceProvider, TService> factory) where TService : class =>
        Add(new ServiceDescriptor(typeof(TService), null, null, sp => factory(sp), ServiceLifetime.Singleton));

    public ServiceCollection AddScoped<TService>() where TService : class =>
        Add(new ServiceDescriptor(typeof(TService), typeof(TService), null, null, ServiceLifetime.Scoped));

    public ServiceCollection AddScoped<TService, TImplementation>() where TImplementation : class, TService =>
        Add(new ServiceDescriptor(typeof(TService), typeof(TImplementation), null, null, ServiceLifetime.Scoped));

    public ServiceCollection AddScoped<TService>(Func<ServiceProvider, TService> factory) where TService : class =>
        Add(new ServiceDescriptor(typeof(TService), null, null, sp => factory(sp), ServiceLifetime.Scoped));

    public ServiceCollection AddTransient<TService>() where TService : class =>
        Add(new ServiceDescriptor(typeof(TService), typeof(TService), null, null, ServiceLifetime.Transient));

    public ServiceCollection AddTransient<TService, TImplementation>() where TImplementation : class, TService =>
        Add(new ServiceDescriptor(typeof(TService), typeof(TImplementation), null, null, ServiceLifetime.Transient));

    public ServiceCollection AddTransient<TService>(Func<ServiceProvider, TService> factory) where TService : class =>
        Add(new ServiceDescriptor(typeof(TService), null, null, sp => factory(sp), ServiceLifetime.Transient));

    public int Count => Descriptors.Count;
}

/// <summary>Resolves services, by constructor and by <c>[Inject]</c> property.</summary>
public sealed class ServiceProvider
{
    private readonly Dictionary<Type, ServiceDescriptor> _byType = new();

    internal ServiceProvider(ServiceCollection services)
    {
        // Last registration wins, as in Microsoft.Extensions.DependencyInjection.
        foreach (var d in services.Descriptors) _byType[d.ServiceType] = d;
        _byType[typeof(ServiceProvider)] = new ServiceDescriptor(typeof(ServiceProvider), null, this, null, ServiceLifetime.Singleton);
    }

    public object? GetService(Type serviceType)
    {
        if (!_byType.TryGetValue(serviceType, out var d)) return null;
        if (d.Instance is not null) return d.Instance;
        var created = d.Factory is not null ? d.Factory(this) : Create(d.ImplementationType!);
        if (d.Lifetime != ServiceLifetime.Transient) d.Instance = created;
        return created;
    }

    public T? GetService<T>() where T : class => GetService(typeof(T)) as T;

    public T GetRequiredService<T>() where T : class =>
        GetService<T>() ?? throw new InvalidOperationException("No service registered for " + typeof(T).Name + ".");

    /// <summary>Creates <paramref name="type"/>, filling constructor parameters from the container.</summary>
    public object Create(Type type)
    {
        ConstructorInfo? best = null;
        var bestCount = -1;
        foreach (var ctor in type.GetConstructors())
        {
            var count = ctor.GetParameters().Length;
            if (count > bestCount)
            {
                best = ctor;
                bestCount = count;
            }
        }
        if (best is null || bestCount == 0) return InjectProperties(Activator.CreateInstance(type)!);
        var parameters = best.GetParameters();
        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            args[i] = GetService(parameters[i].ParameterType)
                ?? throw new InvalidOperationException("Cannot resolve " + parameters[i].ParameterType.Name + " for " + type.Name + ".");
        }
        return InjectProperties(best.Invoke(args));
    }

    /// <summary>Assigns every <c>[Inject]</c> property.</summary>
    public object InjectProperties(object target)
    {
        var descriptor = ComponentDescriptor.For(target.GetType());
        foreach (var p in descriptor.Injected)
        {
            var service = GetService(p.PropertyType)
                ?? throw new InvalidOperationException("Cannot provide a value for property '" + p.Name + "' on type '" + target.GetType().Name + "'. There is no registered service of type '" + p.PropertyType.Name + "'.");
            p.SetValue(target, service);
        }
        return target;
    }
}
