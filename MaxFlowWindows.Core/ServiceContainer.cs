using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
    public Func<object?>? Factory { get; }
    public object? SingletonInstance { get; }

    public ServiceDescriptor(
        Type serviceType,
        Type implementationType,
        ServiceLifetime lifetime,
        Func<object?>? factory = null,
        object? singletonInstance = null)
    {
        ServiceType = serviceType;
        ImplementationType = implementationType;
        Lifetime = lifetime;
        Factory = factory;
        SingletonInstance = singletonInstance;
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
    private sealed class ResolutionFrame
    {
        public Type ServiceType { get; }
        public ResolutionFrame? Parent { get; }

        public ResolutionFrame(Type serviceType, ResolutionFrame? parent)
        {
            ServiceType = serviceType;
            Parent = parent;
        }
    }

    private sealed class SingletonEntry
    {
        public object SyncRoot { get; } = new();
        public object? Instance { get; set; }
        public bool IsInitialized { get; set; }
        public bool IsInitializing { get; set; }
        public int OwnerThreadId { get; set; }
    }

    private readonly object _registrationLock = new();
    private readonly object _waitGraphLock = new();
    private readonly Dictionary<Type, ServiceDescriptor> _registrations = new();
    private readonly Dictionary<int, int> _threadWaitsFor = new();
    private readonly ConcurrentDictionary<Type, SingletonEntry> _singletons = new();
    private readonly AsyncLocal<ResolutionFrame?> _currentResolution = new();

    public ServiceRegistryBuilder<TService, TImplementation> Register<TService, TImplementation>()
        where TImplementation : TService
    {
        return new ServiceRegistryBuilder<TService, TImplementation>(this);
    }

    internal void AddRegistration(Type serviceType, Type implementationType, ServiceLifetime lifetime)
    {
        AddDescriptor(new ServiceDescriptor(serviceType, implementationType, lifetime));
    }

    public void RegisterFactory<TService>(Func<TService> factory, ServiceLifetime lifetime = ServiceLifetime.Transient)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(factory);

        AddDescriptor(new ServiceDescriptor(
            typeof(TService),
            typeof(TService),
            lifetime,
            factory: () => factory()));
    }

    public void RegisterInstance<TService>(TService instance)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(instance);

        AddDescriptor(new ServiceDescriptor(
            typeof(TService),
            instance.GetType(),
            ServiceLifetime.Singleton,
            singletonInstance: instance));
    }

    public TService Resolve<TService>() where TService : notnull
    {
        return (TService)ResolveInternal(typeof(TService));
    }

    public bool IsRegistered<TService>()
    {
        lock (_registrationLock)
        {
            return _registrations.ContainsKey(typeof(TService));
        }
    }

    private void AddDescriptor(ServiceDescriptor descriptor)
    {
        lock (_registrationLock)
        {
            if (_registrations.ContainsKey(descriptor.ServiceType))
                throw new InvalidOperationException($"Service '{descriptor.ServiceType.Name}' is already registered.");

            _registrations.Add(descriptor.ServiceType, descriptor);
        }
    }

    private object ResolveInternal(Type serviceType)
    {
        var descriptor = GetDescriptor(serviceType);

        if (IsResolving(serviceType))
            throw new InvalidOperationException($"Circular dependency detected: {FormatResolutionPath(serviceType)}.");

        if (descriptor.Lifetime == ServiceLifetime.Singleton)
            return ResolveSingleton(descriptor);

        return CreateInstance(descriptor);
    }

    private ServiceDescriptor GetDescriptor(Type serviceType)
    {
        lock (_registrationLock)
        {
            if (!_registrations.TryGetValue(serviceType, out var descriptor))
                throw new InvalidOperationException($"Service '{serviceType.Name}' is not registered.");

            return descriptor;
        }
    }

    private object ResolveSingleton(ServiceDescriptor descriptor)
    {
        var entry = _singletons.GetOrAdd(descriptor.ServiceType, _ => new SingletonEntry());
        var currentThreadId = Environment.CurrentManagedThreadId;

        while (true)
        {
            lock (entry.SyncRoot)
            {
                if (entry.IsInitialized)
                    return entry.Instance!;

                if (entry.IsInitializing)
                {
                    WaitForSingleton(entry, descriptor.ServiceType, currentThreadId);
                    continue;
                }

                entry.IsInitializing = true;
                entry.OwnerThreadId = currentThreadId;
            }

            try
            {
                var instance = CreateInstance(descriptor);

                lock (entry.SyncRoot)
                {
                    entry.Instance = instance;
                    entry.IsInitialized = true;
                    entry.IsInitializing = false;
                    entry.OwnerThreadId = 0;
                    Monitor.PulseAll(entry.SyncRoot);
                }

                return instance;
            }
            catch
            {
                lock (entry.SyncRoot)
                {
                    entry.IsInitializing = false;
                    entry.OwnerThreadId = 0;
                    Monitor.PulseAll(entry.SyncRoot);
                }

                throw;
            }
        }
    }

    private void WaitForSingleton(SingletonEntry entry, Type serviceType, int currentThreadId)
    {
        var ownerThreadId = entry.OwnerThreadId;
        if (ownerThreadId == currentThreadId)
        {
            throw new InvalidOperationException(
                $"Circular dependency detected while resolving singleton '{serviceType.Name}'.");
        }

        RegisterThreadWait(currentThreadId, ownerThreadId, serviceType);
        try
        {
            Monitor.Wait(entry.SyncRoot);
        }
        finally
        {
            ClearThreadWait(currentThreadId, ownerThreadId);
        }
    }

    private void RegisterThreadWait(int waitingThreadId, int ownerThreadId, Type serviceType)
    {
        lock (_waitGraphLock)
        {
            _threadWaitsFor[waitingThreadId] = ownerThreadId;

            var threadId = ownerThreadId;
            while (true)
            {
                if (threadId == waitingThreadId)
                {
                    _threadWaitsFor.Remove(waitingThreadId);
                    throw new InvalidOperationException(
                        $"Circular dependency detected while resolving singleton '{serviceType.Name}'.");
                }

                if (!_threadWaitsFor.TryGetValue(threadId, out threadId))
                    return;
            }
        }
    }

    private void ClearThreadWait(int waitingThreadId, int ownerThreadId)
    {
        lock (_waitGraphLock)
        {
            if (_threadWaitsFor.TryGetValue(waitingThreadId, out var recordedOwnerThreadId)
                && recordedOwnerThreadId == ownerThreadId)
            {
                _threadWaitsFor.Remove(waitingThreadId);
            }
        }
    }

    private object CreateInstance(ServiceDescriptor descriptor)
    {
        var previousFrame = _currentResolution.Value;
        _currentResolution.Value = new ResolutionFrame(descriptor.ServiceType, previousFrame);

        try
        {
            if (descriptor.Factory is not null)
            {
                return descriptor.Factory()
                    ?? throw new InvalidOperationException(
                        $"Factory for service '{descriptor.ServiceType.Name}' returned null.");
            }

            if (descriptor.SingletonInstance is not null)
                return descriptor.SingletonInstance;

            var constructors = descriptor.ImplementationType
                .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(constructor => constructor.GetParameters().Length)
                .ThenBy(constructor => constructor.MetadataToken)
                .ToArray();

            if (constructors.Length == 0)
                throw new InvalidOperationException(
                    $"No public constructor found for '{descriptor.ImplementationType.Name}'.");

            var targetConstructor = constructors[0];
            var parameters = targetConstructor.GetParameters();
            var resolvedParameters = new object[parameters.Length];

            for (var index = 0; index < parameters.Length; index++)
            {
                resolvedParameters[index] = ResolveInternal(parameters[index].ParameterType);
            }

            return targetConstructor.Invoke(resolvedParameters);
        }
        finally
        {
            _currentResolution.Value = previousFrame;
        }
    }

    private bool IsResolving(Type serviceType)
    {
        for (var frame = _currentResolution.Value; frame is not null; frame = frame.Parent)
        {
            if (frame.ServiceType == serviceType)
                return true;
        }

        return false;
    }

    private string FormatResolutionPath(Type repeatedServiceType)
    {
        var path = new List<string>();
        for (var frame = _currentResolution.Value; frame is not null; frame = frame.Parent)
        {
            path.Add(frame.ServiceType.Name);
        }

        path.Reverse();
        path.Add(repeatedServiceType.Name);
        return string.Join(" -> ", path);
    }

}
