using System.Collections.Concurrent;
using MaxFlowWindows.Core;
using Xunit;

namespace Speak.Tests;

public sealed class ServiceContainerTests
{
    public interface ILogger
    {
        string Log(string message);
    }

    public sealed class ConsoleLogger : ILogger
    {
        public string Log(string message) => $"LOG: {message}";
    }

    public interface IConfig
    {
        string Read(string key);
    }

    public sealed class AppConfig : IConfig
    {
        public string Read(string key) => $"value-{key}";
    }

    public sealed class LoggingService
    {
        public int InstanceId { get; }

        private static int _nextId;

        public LoggingService()
        {
            InstanceId = Interlocked.Increment(ref _nextId);
        }
    }

    public interface IRepository
    {
        string Fetch();
    }

    public sealed class SqlRepository : IRepository
    {
        public string Fetch() => "data";
    }

    public sealed class UserService
    {
        public IRepository Repository { get; }
        public ILogger Logger { get; }

        public UserService(IRepository repository, ILogger logger)
        {
            Repository = repository;
            Logger = logger;
        }
    }

    public interface INotification { }

    public sealed class EmailNotification : INotification { }

    public sealed class CompositeService
    {
        public INotification Notification { get; }

        public CompositeService(INotification notification)
        {
            Notification = notification;
        }
    }

    public sealed class FactoryCreatedService
    {
        public ILogger Logger { get; }

        public FactoryCreatedService(ILogger logger)
        {
            Logger = logger;
        }
    }

    public sealed class CircularServiceA
    {
        public CircularServiceA(CircularServiceB dependency)
        {
        }
    }

    public sealed class CircularServiceB
    {
        public CircularServiceB(CircularServiceA dependency)
        {
        }
    }

    public sealed class ConcurrentCircularServiceA
    {
        public ConcurrentCircularServiceA(ConcurrentCircularServiceB dependency)
        {
        }
    }

    public sealed class ConcurrentCircularServiceB
    {
        public ConcurrentCircularServiceB(ConcurrentCircularServiceA dependency)
        {
        }
    }

    public sealed class GreedyConstructorService
    {
        public string ConstructorUsed { get; }
        public ILogger? Logger { get; }

        public GreedyConstructorService()
        {
            ConstructorUsed = "parameterless";
        }

        public GreedyConstructorService(ILogger logger)
        {
            ConstructorUsed = "dependency";
            Logger = logger;
        }
    }

    public sealed class MissingConstructorDependency
    {
    }

    public sealed class GreedyConstructorWithMissingDependency
    {
        public GreedyConstructorWithMissingDependency()
        {
        }

        public GreedyConstructorWithMissingDependency(MissingConstructorDependency dependency)
        {
        }
    }

    public sealed class NoPublicConstructorService
    {
        private NoPublicConstructorService()
        {
        }
    }

    [Fact]
    public void Resolve_Transient_ReturnsNewInstanceEachTime()
    {
        var container = new ServiceContainer();
        container.Register<ILogger, ConsoleLogger>().AsTransient();

        var first = container.Resolve<ILogger>();
        var second = container.Resolve<ILogger>();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Resolve_Singleton_ReturnsSameInstance()
    {
        var container = new ServiceContainer();
        container.Register<ILogger, ConsoleLogger>().AsSingleton();

        var first = container.Resolve<ILogger>();
        var second = container.Resolve<ILogger>();

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void RegisterInstance_AlwaysReturnsSameInstance()
    {
        var container = new ServiceContainer();
        var logger = new ConsoleLogger();
        container.RegisterInstance<ILogger>(logger);

        var resolved = container.Resolve<ILogger>();

        Assert.Same(logger, resolved);
    }

    [Fact]
    public void Resolve_WithConstructorInjection_InjectsDependencies()
    {
        var container = new ServiceContainer();
        container.Register<IRepository, SqlRepository>().AsTransient();
        container.Register<ILogger, ConsoleLogger>().AsSingleton();
        container.Register<UserService, UserService>().AsTransient();

        var service = container.Resolve<UserService>();

        Assert.NotNull(service);
        Assert.NotNull(service.Repository);
        Assert.NotNull(service.Logger);
    }

    [Fact]
    public void Resolve_SingletonWithDependencies_ResolvesWithoutLockRecursion()
    {
        var container = new ServiceContainer();
        container.Register<IRepository, SqlRepository>().AsTransient();
        container.Register<ILogger, ConsoleLogger>().AsSingleton();
        container.Register<UserService, UserService>().AsSingleton();

        var first = container.Resolve<UserService>();
        var second = container.Resolve<UserService>();

        Assert.Same(first, second);
        Assert.IsType<SqlRepository>(first.Repository);
        Assert.Same(first.Logger, second.Logger);
    }

    [Fact]
    public void Resolve_UnregisteredService_ThrowsInvalidOperationException()
    {
        var container = new ServiceContainer();

        var ex = Assert.Throws<InvalidOperationException>(() => container.Resolve<ILogger>());
        Assert.Contains("not registered", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_DuplicateService_ThrowsInvalidOperationException()
    {
        var container = new ServiceContainer();
        container.Register<ILogger, ConsoleLogger>().AsTransient();

        var ex = Assert.Throws<InvalidOperationException>(
            () => container.Register<ILogger, ConsoleLogger>().AsTransient());
        Assert.Contains("already registered", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RegisterInstance_DuplicateService_ThrowsInvalidOperationException()
    {
        var container = new ServiceContainer();
        container.RegisterInstance<ILogger>(new ConsoleLogger());

        var ex = Assert.Throws<InvalidOperationException>(
            () => container.RegisterInstance<ILogger>(new ConsoleLogger()));
        Assert.Contains("already registered", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RegisterFactory_Transient_CallsFactoryEachTime()
    {
        var container = new ServiceContainer();
        var callCount = 0;
        container.RegisterFactory(() =>
        {
            callCount++;
            return new LoggingService();
        }, ServiceLifetime.Transient);

        var first = container.Resolve<LoggingService>();
        var second = container.Resolve<LoggingService>();

        Assert.NotSame(first, second);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public void RegisterFactory_DefaultLifetime_IsTransient()
    {
        var container = new ServiceContainer();
        container.RegisterFactory(() => new LoggingService());

        Assert.NotSame(container.Resolve<LoggingService>(), container.Resolve<LoggingService>());
    }

    [Fact]
    public void RegisterFactory_Singleton_CallsFactoryOnce()
    {
        var container = new ServiceContainer();
        var callCount = 0;
        container.RegisterFactory(() =>
        {
            callCount++;
            return new LoggingService();
        }, ServiceLifetime.Singleton);

        var first = container.Resolve<LoggingService>();
        var second = container.Resolve<LoggingService>();

        Assert.Same(first, second);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void RegisterFactory_Singleton_CanReenterContainerAndResolveDependency()
    {
        var container = new ServiceContainer();
        container.Register<ILogger, ConsoleLogger>().AsSingleton();
        container.RegisterFactory(
            () => new FactoryCreatedService(container.Resolve<ILogger>()),
            ServiceLifetime.Singleton);

        var first = container.Resolve<FactoryCreatedService>();
        var second = container.Resolve<FactoryCreatedService>();

        Assert.Same(first, second);
        Assert.Same(container.Resolve<ILogger>(), first.Logger);
    }

    [Fact]
    public void RegisterFactory_NullResult_ThrowsClearExceptionAndIsNotCached()
    {
        var container = new ServiceContainer();
        var attempts = 0;
        container.RegisterFactory<LoggingService>(() =>
        {
            attempts++;
            return null!;
        }, ServiceLifetime.Singleton);

        var first = Assert.Throws<InvalidOperationException>(() => container.Resolve<LoggingService>());
        var second = Assert.Throws<InvalidOperationException>(() => container.Resolve<LoggingService>());

        Assert.Contains("returned null", first.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("returned null", second.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void Resolve_FailedSingletonFactory_CanRetrySuccessfully()
    {
        var container = new ServiceContainer();
        var attempts = 0;
        container.RegisterFactory(() =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new InvalidOperationException("temporary failure");

            return new LoggingService();
        }, ServiceLifetime.Singleton);

        var failure = Assert.Throws<InvalidOperationException>(() => container.Resolve<LoggingService>());
        var resolved = container.Resolve<LoggingService>();

        Assert.Equal("temporary failure", failure.Message);
        Assert.NotNull(resolved);
        Assert.Same(resolved, container.Resolve<LoggingService>());
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void IsRegistered_ReturnsTrueForRegisteredService()
    {
        var container = new ServiceContainer();
        container.Register<ILogger, ConsoleLogger>().AsSingleton();

        Assert.True(container.IsRegistered<ILogger>());
    }

    [Fact]
    public void IsRegistered_ReturnsFalseForUnregisteredService()
    {
        var container = new ServiceContainer();
        Assert.False(container.IsRegistered<ILogger>());
    }

    [Fact]
    public void Singleton_ThreadSafe_ReturnsSameInstanceFromMultipleThreads()
    {
        var container = new ServiceContainer();
        container.Register<IConfig, AppConfig>().AsSingleton();

        var results = new ConcurrentBag<IConfig>();
        var threads = Enumerable.Range(0, 10).Select(_ => new Thread(() =>
        {
            results.Add(container.Resolve<IConfig>());
        })).ToList();

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        var first = results.First();
        Assert.All(results, result => Assert.Same(first, result));
    }

    [Fact]
    public void Singleton_ConcurrentResolution_ConstructsExactlyOnce()
    {
        const int threadCount = 16;
        var container = new ServiceContainer();
        var constructionCount = 0;
        using var start = new ManualResetEventSlim();
        var results = new ConcurrentBag<LoggingService>();

        container.RegisterFactory(() =>
        {
            Interlocked.Increment(ref constructionCount);
            Thread.Sleep(50);
            return new LoggingService();
        }, ServiceLifetime.Singleton);

        var threads = Enumerable.Range(0, threadCount)
            .Select(_ => new Thread(() =>
            {
                start.Wait();
                results.Add(container.Resolve<LoggingService>());
            }))
            .ToList();

        foreach (var thread in threads) thread.Start();
        start.Set();
        foreach (var thread in threads) Assert.True(thread.Join(TimeSpan.FromSeconds(5)));

        var first = results.First();
        Assert.Equal(threadCount, results.Count);
        Assert.All(results, result => Assert.Same(first, result));
        Assert.Equal(1, constructionCount);
    }

    [Fact]
    public void Transient_ThreadSafe_ResolvesWithoutException()
    {
        var container = new ServiceContainer();
        container.Register<ILogger, ConsoleLogger>().AsTransient();

        var exceptions = new ConcurrentBag<Exception>();
        var threads = Enumerable.Range(0, 10).Select(_ => new Thread(() =>
        {
            try { container.Resolve<ILogger>(); }
            catch (Exception ex) { exceptions.Add(ex); }
        })).ToList();

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        Assert.Empty(exceptions);
    }

    [Fact]
    public void Resolve_TransientCircularDependency_ThrowsClearException()
    {
        var container = new ServiceContainer();
        container.Register<CircularServiceA, CircularServiceA>().AsTransient();
        container.Register<CircularServiceB, CircularServiceB>().AsTransient();

        var ex = Assert.Throws<InvalidOperationException>(() => container.Resolve<CircularServiceA>());

        Assert.Contains("Circular dependency", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CircularServiceA -> CircularServiceB -> CircularServiceA", ex.Message);
    }

    [Fact]
    public void Resolve_SingletonCircularDependency_ThrowsClearExceptionAndDoesNotPoisonRetry()
    {
        var container = new ServiceContainer();
        container.Register<CircularServiceA, CircularServiceA>().AsSingleton();
        container.Register<CircularServiceB, CircularServiceB>().AsSingleton();

        var first = Assert.Throws<InvalidOperationException>(() => container.Resolve<CircularServiceA>());
        var second = Assert.Throws<InvalidOperationException>(() => container.Resolve<CircularServiceA>());

        Assert.Contains("Circular dependency", first.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Circular dependency", second.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_ConcurrentSingletonCircularDependency_FailsWithoutDeadlock()
    {
        var container = new ServiceContainer();
        using var aFactoryStarted = new ManualResetEventSlim();
        using var bFactoryStarted = new ManualResetEventSlim();
        var exceptions = new ConcurrentBag<Exception>();

        container.RegisterFactory(() =>
        {
            aFactoryStarted.Set();
            Assert.True(bFactoryStarted.Wait(TimeSpan.FromSeconds(5)));
            return new ConcurrentCircularServiceA(container.Resolve<ConcurrentCircularServiceB>());
        }, ServiceLifetime.Singleton);
        container.RegisterFactory(() =>
        {
            bFactoryStarted.Set();
            Assert.True(aFactoryStarted.Wait(TimeSpan.FromSeconds(5)));
            return new ConcurrentCircularServiceB(container.Resolve<ConcurrentCircularServiceA>());
        }, ServiceLifetime.Singleton);

        var aThread = new Thread(() =>
        {
            try { container.Resolve<ConcurrentCircularServiceA>(); }
            catch (Exception ex) { exceptions.Add(ex); }
        });
        var bThread = new Thread(() =>
        {
            try { container.Resolve<ConcurrentCircularServiceB>(); }
            catch (Exception ex) { exceptions.Add(ex); }
        });

        aThread.Start();
        bThread.Start();

        Assert.True(aThread.Join(TimeSpan.FromSeconds(5)));
        Assert.True(bThread.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, exceptions.Count);
        Assert.All(exceptions, exception =>
            Assert.Contains("Circular dependency", exception.Message, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_SingletonFactorySelfResolution_ThrowsCircularDependency()
    {
        var container = new ServiceContainer();
        container.RegisterFactory(
            () => container.Resolve<LoggingService>(),
            ServiceLifetime.Singleton);

        var ex = Assert.Throws<InvalidOperationException>(() => container.Resolve<LoggingService>());

        Assert.Contains("Circular dependency", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LoggingService -> LoggingService", ex.Message);
    }

    [Fact]
    public void ConstructorInjection_MultipleParameters_ResolvesCorrectly()
    {
        var container = new ServiceContainer();
        container.Register<IRepository, SqlRepository>().AsTransient();
        container.Register<ILogger, ConsoleLogger>().AsSingleton();
        container.Register<UserService, UserService>().AsTransient();

        var service = container.Resolve<UserService>();

        Assert.Equal("data", service.Repository.Fetch());
        Assert.Equal("LOG: test", service.Logger.Log("test"));
    }

    [Fact]
    public void ConstructorSelection_UsesGreediestPublicConstructor()
    {
        var container = new ServiceContainer();
        container.Register<ILogger, ConsoleLogger>().AsSingleton();
        container.Register<GreedyConstructorService, GreedyConstructorService>().AsTransient();

        var service = container.Resolve<GreedyConstructorService>();

        Assert.Equal("dependency", service.ConstructorUsed);
        Assert.Same(container.Resolve<ILogger>(), service.Logger);
    }

    [Fact]
    public void ConstructorSelection_DoesNotFallBackWhenGreediestDependencyIsMissing()
    {
        var container = new ServiceContainer();
        container.Register<GreedyConstructorWithMissingDependency, GreedyConstructorWithMissingDependency>().AsTransient();

        var ex = Assert.Throws<InvalidOperationException>(
            () => container.Resolve<GreedyConstructorWithMissingDependency>());

        Assert.Contains(nameof(MissingConstructorDependency), ex.Message);
        Assert.Contains("not registered", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConstructorSelection_NoPublicConstructor_ThrowsClearException()
    {
        var container = new ServiceContainer();
        container.Register<NoPublicConstructorService, NoPublicConstructorService>().AsTransient();

        var ex = Assert.Throws<InvalidOperationException>(
            () => container.Resolve<NoPublicConstructorService>());

        Assert.Contains("No public constructor", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nameof(NoPublicConstructorService), ex.Message);
    }

}
