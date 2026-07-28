using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;

namespace MaxFlowWindows.Core;

public enum ServiceLifetime
{
    Singleton,
    Transient,
}

internal sealed class ServiceDescriptor
{
    public Type ServiceType { get; }
    public Type ImplementationType { get; }
    public ServiceLifetime Lifetime { get; }
    public Func<object>? Factory { get; set; }
    public object? SingletonInstance { get; set; }

    public ServiceDescriptor(Type serviceType, Type implementationType, ServiceLifetime lifetime)
    {
        ServiceType = serviceType;
        ImplementationType = implementationType;
        Lifetime = lifetime;
    }
}

public sealed class ServiceRegistryBuilder<TService, TImplementation>
    where TImplementation : TService
{
    private readonly ServiceContainer _container;

    internal ServiceRegistryBuilder(ServiceContainer container)
    {
        _container = container;
    }

    public void AsSingleton()
    {
        _container.AddRegistration(typeof(TService), typeof(TImplementation), ServiceLifetime.Singleton);
    }

    public void AsTransient()
    {
        _container.AddRegistration(typeof(TService), typeof(TImplementation), ServiceLifetime.Transient);
    }
}

public sealed class ServiceContainer
{
    private readonly ConcurrentDictionary<Type, ServiceDescriptor> _registrations = new();
    private readonly ConcurrentDictionary<Type, object> _singletons = new();
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly Dictionary<Type, object> _syncSingletons = new();

    public ServiceRegistryBuilder<TService, TImplementation> Register<TService, TImplementation>()
        where TImplementation : TService
    {
        var builder = new ServiceRegistryBuilder<TService, TImplementation>(this);
        return builder;
    }

    internal void AddRegistration(Type serviceType, Type implementationType, ServiceLifetime lifetime)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_registrations.ContainsKey(serviceType))
                throw new InvalidOperationException($"Service '{serviceType.Name}' is already registered.");

            var descriptor = new ServiceDescriptor(serviceType, implementationType, lifetime);
            _registrations[serviceType] = descriptor;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void RegisterFactory<TService>(Func<TService> factory, ServiceLifetime lifetime = ServiceLifetime.Transient)
        where TService : class
    {
        _lock.EnterWriteLock();
        try
        {
            if (_registrations.ContainsKey(typeof(TService)))
                throw new InvalidOperationException($"Service '{typeof(TService).Name}' is already registered.");

            var descriptor = new ServiceDescriptor(typeof(TService), typeof(TService), lifetime)
            {
                Factory = () => factory()!,
            };
            _registrations[typeof(TService)] = descriptor;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void RegisterInstance<TService>(TService instance)
        where TService : class
    {
        _lock.EnterWriteLock();
        try
        {
            if (_registrations.ContainsKey(typeof(TService)))
                throw new InvalidOperationException($"Service '{typeof(TService).Name}' is already registered.");

            var descriptor = new ServiceDescriptor(typeof(TService), instance.GetType(), ServiceLifetime.Singleton)
            {
                SingletonInstance = instance,
            };
            _registrations[typeof(TService)] = descriptor;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public TService Resolve<TService>() where TService : notnull
    {
        var resolveContext = new HashSet<Type>();
        return (TService)ResolveInternal(typeof(TService), resolveContext);
    }

    public bool IsRegistered<TService>()
    {
        _lock.EnterReadLock();
        try
        {
            return _registrations.ContainsKey(typeof(TService));
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private object ResolveInternal(Type serviceType, HashSet<Type> resolveContext)
    {
        _lock.EnterReadLock();
        ServiceDescriptor? descriptor;
        try
        {
            if (!_registrations.TryGetValue(serviceType, out descriptor))
                throw new InvalidOperationException($"Service '{serviceType.Name}' is not registered.");
        }
        finally
        {
            _lock.ExitReadLock();
        }

        if (descriptor.Lifetime == ServiceLifetime.Singleton)
        {
            _lock.EnterReadLock();
            bool hasInstance = _syncSingletons.TryGetValue(serviceType, out var existing);
            _lock.ExitReadLock();

            if (hasInstance)
                return existing!;

            _lock.EnterWriteLock();
            try
            {
                if (_syncSingletons.TryGetValue(serviceType, out existing))
                    return existing!;

                var instance = CreateInstance(descriptor, resolveContext);
                _syncSingletons[serviceType] = instance;
                return instance;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        return CreateInstance(descriptor, resolveContext);
    }

    private object CreateInstance(ServiceDescriptor descriptor, HashSet<Type> resolveContext)
    {
        if (descriptor.Factory is not null)
            return descriptor.Factory();

        if (descriptor.SingletonInstance is not null)
            return descriptor.SingletonInstance;

        if (!resolveContext.Add(descriptor.ServiceType))
            throw new InvalidOperationException($"Circular dependency detected for '{descriptor.ServiceType.Name}'.");

        try
        {
            var constructors = descriptor.ImplementationType
                .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .ToArray();

            if (constructors.Length == 0)
                throw new InvalidOperationException($"No public constructor found for '{descriptor.ImplementationType.Name}'.");

            ConstructorInfo targetCtor = constructors[0];
            var parameters = targetCtor.GetParameters();
            var resolvedParams = new object[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                resolvedParams[i] = ResolveInternal(parameters[i].ParameterType, resolveContext);
            }

            var instance = targetCtor.Invoke(resolvedParams);
            return instance;
        }
        finally
        {
            resolveContext.Remove(descriptor.ServiceType);
        }
    }

    public void Build()
    {
    }
}
