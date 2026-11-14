using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// An <see cref="IHostBuilder"/> that builds and starts the host by running <typeparamref name="TEntryPoint"/>'s entry
/// point, like the deferred builder <c>WebApplicationFactory</c> uses for a minimal-hosting <c>Program</c>, except for
/// how it learns the outcome of the start.
/// </summary>
/// <remarks>
/// <para>The deferred builder lets the entry point run on once the host is built, and only then, on the test thread,
/// resolves <see cref="IHostApplicationLifetime"/> from the host's provider to wait for <c>ApplicationStarted</c>. When the
/// start fails (an options validator run by <c>ValidateOnStart</c>), the entry point's <c>Run</c> disposes the host as it
/// unwinds. Whenever that happens before the test thread gets to the provider, the factory throws
/// <see cref="ObjectDisposedException"/> (<c>IServiceProvider</c>) and the start's own exception is lost. On a loaded
/// runner that was 99 runs in 100 (timed-tests-hold-on-a-loaded-runner F6).</para>
/// <para>Here both outcomes are wired on the entry point's own thread before <c>Run</c> can begin: <c>ApplicationStarted</c>
/// is registered while the <c>HostBuilt</c> event is delivered, and the entry point's exit, normal or not, completes a
/// task of its own. <see cref="IHost.StartAsync"/> awaits whichever comes first and never touches the provider, so a
/// failed start throws its own exception however late the test thread runs.</para>
/// <para>The configuration the factory records is replayed onto the application's builder exactly as the deferred builder
/// does it: host configuration as command-line arguments, everything else at <c>HostBuilding</c>.</para>
/// </remarks>
internal sealed class EntryPointHostBuilder<TEntryPoint> : IHostBuilder
    where TEntryPoint : class
{
    private const string HostingListenerName = "Microsoft.Extensions.Hosting";

    // The same guard WebApplicationFactory's resolver has against an entry point that never builds a host. It bounds a
    // hang, not a race: the host is built before Run, whatever the load.
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);

    // A builder, read once at Build: the factory's settings reach it through a chained live configuration, so
    // what they hold when the host is built is what the entry point gets.
    private readonly ConfigurationBuilder _hostConfiguration = new();
    private Action<IHostBuilder> _configure;

    public EntryPointHostBuilder()
    {
        _configure = builder =>
        {
            foreach (var (key, value) in Properties)
                builder.Properties[key] = value;
        };
    }

    public IDictionary<object, object> Properties { get; } = new Dictionary<object, object>();

    public IHostBuilder ConfigureHostConfiguration(Action<IConfigurationBuilder> configureDelegate)
    {
        configureDelegate(_hostConfiguration);
        return this;
    }

    public IHostBuilder ConfigureAppConfiguration(Action<HostBuilderContext, IConfigurationBuilder> configureDelegate)
    {
        _configure += builder => builder.ConfigureAppConfiguration(configureDelegate);
        return this;
    }

    public IHostBuilder ConfigureServices(Action<HostBuilderContext, IServiceCollection> configureDelegate)
    {
        _configure += builder => builder.ConfigureServices(configureDelegate);
        return this;
    }

    public IHostBuilder ConfigureContainer<TContainerBuilder>(Action<HostBuilderContext, TContainerBuilder> configureDelegate)
    {
        _configure += builder => builder.ConfigureContainer(configureDelegate);
        return this;
    }

    public IHostBuilder UseServiceProviderFactory<TContainerBuilder>(IServiceProviderFactory<TContainerBuilder> factory)
        where TContainerBuilder : notnull
    {
        _configure += builder => builder.UseServiceProviderFactory(factory);
        return this;
    }

    public IHostBuilder UseServiceProviderFactory<TContainerBuilder>(
        Func<HostBuilderContext, IServiceProviderFactory<TContainerBuilder>> factory)
        where TContainerBuilder : notnull
    {
        _configure += builder => builder.UseServiceProviderFactory(factory);
        return this;
    }

    public IHost Build()
    {
        var entryPoint = typeof(TEntryPoint).Assembly.EntryPoint
            ?? throw new InvalidOperationException($"{typeof(TEntryPoint).Assembly.GetName().Name} has no entry point.");
        string[] args;
        using (var hostConfiguration = (ConfigurationRoot)_hostConfiguration.Build())
        {
            args = hostConfiguration.AsEnumerable()
                .Select(setting => string.Create(CultureInfo.InvariantCulture, $"--{setting.Key}={setting.Value}"))
                .ToArray();
        }

        using var run = new EntryPointRun(entryPoint, args, _configure);
        return run.BuildHost();
    }

    /// <summary>One run of the entry point: it builds the host, and its thread reports the start's outcome.</summary>
    private sealed class EntryPointRun(MethodInfo entryPoint, string[] args, Action<IHostBuilder> configure)
        : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        // Other tests build hosts concurrently; only the events raised on this run's entry-point thread are ours.
        private static readonly AsyncLocal<EntryPointRun?> Current = new();

        private readonly TaskCompletionSource<IHost> _built = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IDisposable? _hostingSubscription;

        public EntryPointHost BuildHost()
        {
            using (DiagnosticListener.AllListeners.Subscribe(this))
            {
                var thread = new Thread(RunEntryPoint) { IsBackground = true, Name = "EntryPointHostBuilder" };
                thread.Start();
                try
                {
                    if (!_built.Task.Wait(BuildTimeout))
                        throw new InvalidOperationException(
                            $"The entry point did not build its host within {BuildTimeout.TotalMinutes} minutes.");
                }
                catch (AggregateException) when (_built.Task.IsFaulted)
                {
                    // Rethrown below without the AggregateException wrapper.
                }

                return new EntryPointHost(_built.Task.GetAwaiter().GetResult(), _started.Task, _exited.Task);
            }
        }

        private void RunEntryPoint()
        {
            Current.Value = this;
            try
            {
                entryPoint.Invoke(null, entryPoint.GetParameters().Length == 0 ? [] : [args]);
                _built.TrySetException(new InvalidOperationException("The entry point exited without building a host."));
                _exited.TrySetResult();
            }
            catch (TargetInvocationException invocation)
            {
                var exception = invocation.InnerException ?? invocation;
                _built.TrySetException(exception);
                _exited.TrySetException(exception);
            }
        }

        public void OnNext(DiagnosticListener value)
        {
            if (Current.Value == this && value.Name == HostingListenerName)
                _hostingSubscription = value.Subscribe(this);
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (Current.Value != this)
                return;

            if (value.Key == "HostBuilding")
            {
                configure((IHostBuilder)value.Value!);
            }
            else if (value.Key == "HostBuilt")
            {
                // On the entry-point thread, before Run: the provider is alive, whatever happens to the start next.
                var host = (IHost)value.Value!;
                host.Services.GetRequiredService<IHostApplicationLifetime>()
                    .ApplicationStarted.Register(() => _started.TrySetResult());
                _built.TrySetResult(host);
            }
        }

        // The hosting listener raises nothing this run needs after HostBuilt; Build disposes the run once it returns.
        public void Dispose() => _hostingSubscription?.Dispose();

        public void OnCompleted() => _hostingSubscription?.Dispose();

        public void OnError(Exception error)
        {
        }
    }

    /// <summary>The application's host, whose start is the entry point's: started, or the exception it exited with.</summary>
    private sealed class EntryPointHost(IHost host, Task started, Task exited) : IHost, IAsyncDisposable
    {
        public IServiceProvider Services => host.Services;

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            var first = await Task.WhenAny(started, exited).WaitAsync(cancellationToken).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            if (first == exited)
                throw new InvalidOperationException("The entry point returned before its host started.");
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => host.StopAsync(cancellationToken);

        public void Dispose() => host.Dispose();

        public async ValueTask DisposeAsync()
        {
            if (host is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else
                host.Dispose();
        }
    }
}
