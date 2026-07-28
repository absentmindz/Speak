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
        string Get(string key);
    }

    public sealed class AppConfig : IConfig
    {
        public string Get(string key) => $"value-{key}";
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

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        var first = results.First();
        Assert.All(results, r => Assert.Same(first, r));
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

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        Assert.Empty(exceptions);
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
}
